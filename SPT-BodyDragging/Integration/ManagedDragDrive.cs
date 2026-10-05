using System;
using System.Collections.Generic;
using UnityEngine;

namespace BodyDragging.Integration
{
    internal sealed class ManagedDragSettings
    {
        internal float Slack, HandSpeed, TeleportDistance, HoldError, LimbSpring, MaxAcceleration;
        internal float TargetSmoothing, YawFollow;
        internal bool Valid => Positive(Slack) && Positive(HandSpeed) && Positive(TeleportDistance) && Positive(HoldError) &&
            ManagedIntentBuffer.Finite(LimbSpring) && LimbSpring >= 0 && Positive(MaxAcceleration);
        private static bool Positive(float value) => ManagedIntentBuffer.Finite(value) && value > 0;
        internal static ManagedDragSettings Capture() => new ManagedDragSettings
        {
            Slack = Plugin.HoldSlack.Value, HandSpeed = Plugin.MaxHandSpeed.Value,
            TeleportDistance = Plugin.TeleportDistance.Value, HoldError = Plugin.MaxHoldError.Value,
            LimbSpring = Plugin.GrabSpring.Value * Plugin.LimbFollowStrength.Value,
            MaxAcceleration = Plugin.MaximumGrabAcceleration.Value,
            TargetSmoothing = Mathf.Clamp(Plugin.RemoteTargetSmoothing.Value, 0f, .3f),
            YawFollow = Mathf.Clamp01(Plugin.HoldPoseYawFollow.Value)
        };
    }

    // Remote input/pose reaches Rupture as a staircase; Rupture chases the newest target at a
    // capped hand speed and idles on arrival. A critically-damped chase makes it continuous.
    internal sealed class ManagedTargetSmoother
    {
        // Pose/camera noise below this never moves the hand (KeepMeAlive uses the same 3cm dead zone).
        internal const float DeadZone = 0.03f;
        private Vector3 _value, _velocity;
        private bool _active;
        internal void Reset() { _active = false; _velocity = Vector3.zero; }
        internal Vector3 Step(Vector3 raw, Vector3 start, float smoothTime, float deltaTime, float maxSpeed, float snapDistance)
        {
            if (smoothTime <= 0f) { Reset(); return raw; }
            if (!_active || (raw - _value).sqrMagnitude > snapDistance * snapDistance)
            {
                _value = _active ? raw : start;
                _velocity = Vector3.zero;
                _active = true;
            }
            if ((raw - _value).sqrMagnitude < DeadZone * DeadZone && _velocity.sqrMagnitude < DeadZone * DeadZone)
            {
                _velocity = Vector3.zero;
                return _value;
            }
            _value = Vector3.SmoothDamp(_value, raw, ref _velocity, smoothTime, maxSpeed, deltaTime);
            return _value;
        }
    }

    // State comes exclusively from completed Rupture steps. Camera input can be
    // coalesced; recovery stays pending until the ABI acknowledges its id.
    internal sealed class ManagedDragDrive
    {
        private readonly ManagedDragSettings _settings;
        private const float YawSmoothTime = 0.15f;
        private Vector3[] _initial;
        private Vector3 _initialChest;
        private Vector3 _lastTarget;
        private uint _nextTranslation;
        private float _startYaw, _filteredYaw, _yawVelocity;
        private bool _yawCaptured, _armed = true;
        internal uint TranslationId { get; private set; }
        internal Vector3 Translation { get; private set; }
        internal bool Armed => _armed;
        internal ManagedDragDrive(ManagedDragSettings settings, ProviderView view)
        {
            _settings = settings;
            Capture(view);
            _lastTarget = view.GripPosition;
        }
        private void Capture(ProviderView view)
        {
            _initial = new Vector3[view.Bodies.Length];
            for (int i = 0; i < _initial.Length; i++) _initial[i] = view.Bodies[i].Position;
            _initialChest = view.Bodies[view.GripIndex].Position;
        }
        // The limb-follow shape is a snapshot of the corpse; taking it while the ragdoll is still
        // tumbling bakes a contorted pose in that the springs then fight. Hold off limb assist
        // (the tether still pulls) until the body has quieted, then snapshot.
        internal void Suspend() => _armed = false;
        internal void Arm(ProviderView view)
        {
            Capture(view);
            _yawCaptured = false;
            _armed = true;
        }
        internal ProviderAcceleration[] Compose(ProviderView view, Vector3 target, float elapsed = 0, float yaw = float.NaN)
        {
            if (TranslationId != 0 && view.TranslationId == TranslationId) { TranslationId = 0; Translation = Vector3.zero; }
            ProviderBody chest = view.Bodies[view.GripIndex];
            if (TranslationId == 0)
            {
                Vector3 jump = target - _lastTarget;
                Vector3 offset = jump.sqrMagnitude > _settings.TeleportDistance * _settings.TeleportDistance ? jump : Vector3.zero;
                Vector3 shiftedPoint = chest.CenterOfMass + offset;
                if ((shiftedPoint - target).sqrMagnitude > _settings.HoldError * _settings.HoldError) offset += target - shiftedPoint;
                if (offset.sqrMagnitude > 0)
                {
                    if (_nextTranslation == uint.MaxValue) throw new InvalidOperationException("recovery sequence exhausted");
                    TranslationId = ++_nextTranslation;
                    Translation = offset;
                }
            }
            _lastTarget = target;
            if (_settings.LimbSpring == 0 || !_armed) return Array.Empty<ProviderAcceleration>();
            float swingCos = 1f, swingSin = 0f;
            if (!float.IsNaN(yaw) && ManagedIntentBuffer.Finite(yaw) && _settings.YawFollow > 0f)
            {
                if (!_yawCaptured) { _startYaw = _filteredYaw = yaw; _yawVelocity = 0f; _yawCaptured = true; }
                _filteredYaw = Mathf.SmoothDampAngle(_filteredYaw, yaw, ref _yawVelocity, YawSmoothTime, float.PositiveInfinity, elapsed > 0f ? elapsed : 1f / 60f);
                float radians = Mathf.DeltaAngle(_startYaw, _filteredYaw) * _settings.YawFollow * Mathf.Deg2Rad;
                swingCos = Mathf.Cos(radians); swingSin = Mathf.Sin(radians);
            }
            float damping = 2.2f * (float)Math.Sqrt(Math.Max(_settings.LimbSpring, .01f));
            // Backward-Euler PD avoids the explicit spring's velocity inversion
            // at coarse headless steps. Do not scale the final acceleration by
            // caller dt: Rupture/PhysX integrates it once at its own cadence.
            float step = Math.Max(1f / 30f, Math.Max(view.SimulationDeltaTime, elapsed));
            float denominator = 1f + damping * step + _settings.LimbSpring * step * step;
            List<ProviderAcceleration> forces = new List<ProviderAcceleration>();
            foreach (ProviderBody body in view.Bodies)
            {
                if (!body.Eligible || body.Index == view.GripIndex || body.Index < 0 || body.Index >= _initial.Length) continue;
                Vector3 arm = _initial[body.Index] - _initialChest;
                Vector3 desired = chest.Position + new Vector3(arm.x * swingCos + arm.z * swingSin, arm.y, arm.z * swingCos - arm.x * swingSin);
                Vector3 value = ((desired - body.Position) * _settings.LimbSpring -
                    body.Velocity * (damping + _settings.LimbSpring * step)) / denominator;
                forces.Add(new ProviderAcceleration { Index = body.Index, Value = Vector3.ClampMagnitude(value, _settings.MaxAcceleration) });
            }
            return forces.ToArray();
        }
    }
}
