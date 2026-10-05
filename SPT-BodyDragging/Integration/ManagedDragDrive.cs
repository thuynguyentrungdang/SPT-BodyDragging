using System;
using System.Collections.Generic;
using UnityEngine;

namespace BodyDragging.Integration
{
    internal sealed class ManagedDragSettings
    {
        internal float Slack, HandSpeed, TeleportDistance, HoldError, LimbSpring, MaxAcceleration;
        internal float TargetSmoothing, YawFollow, HeadLeadTurnRate = 120f;
        internal bool HeadLeads;
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
            YawFollow = Mathf.Clamp01(Plugin.HoldPoseYawFollow.Value),
            HeadLeads = Plugin.HeadLeads.Value,
            HeadLeadTurnRate = Mathf.Clamp(Plugin.HeadLeadTurnRate.Value, 30f, 360f)
        };
    }

    // Low-pass for the hold height: terrain raycasts step on rough ground, and a hand that bobs
    // vertically lifts the chest off the floor so the whole body hops.
    internal sealed class ManagedHeightFilter
    {
        private float _value, _velocity;
        private bool _active;
        internal void Reset() { _active = false; _velocity = 0f; }
        internal float Step(float desired, float start, float smoothTime, float deltaTime)
        {
            if (!_active) { _value = start; _velocity = 0f; _active = true; }
            _value = Mathf.SmoothDamp(_value, desired, ref _velocity, smoothTime, float.PositiveInfinity, deltaTime);
            return _value;
        }
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
        private float _startYaw, _filteredYaw, _yawVelocity, _headYaw, _orient;
        private bool _yawCaptured, _armed = true, _hasHeading;
        // Original core-spawner indices of the head and pelvis bodies (-1 = unknown); set before Arm.
        internal int HeadIndex = -1, PelvisIndex = -1;
        internal uint TranslationId { get; private set; }
        internal Vector3 Translation { get; private set; }
        internal bool Armed => _armed;
        internal bool HasHeading => _hasHeading;
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
            _orient = 0f;
            _hasHeading = HeadingYaw(view, out _headYaw);
        }
        // Horizontal yaw of the chest->head axis (pelvis->chest if the head is missing/upright).
        private bool HeadingYaw(ProviderView view, out float yaw)
        {
            if (HeadIndex >= 0 && HeadIndex < view.Bodies.Length && view.Bodies[HeadIndex].Eligible &&
                FlatYaw(view.Bodies[HeadIndex].Position - _initialChest, out yaw)) return true;
            if (PelvisIndex >= 0 && PelvisIndex < view.Bodies.Length && view.Bodies[PelvisIndex].Eligible &&
                FlatYaw(_initialChest - view.Bodies[PelvisIndex].Position, out yaw)) return true;
            yaw = 0f;
            return false;
        }
        private static bool FlatYaw(Vector3 axis, out float yaw)
        {
            axis.y = 0f;
            if (axis.sqrMagnitude < .01f) { yaw = 0f; return false; }
            yaw = Mathf.Atan2(axis.x, axis.z) * Mathf.Rad2Deg;
            return true;
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
            bool headLeads = _settings.HeadLeads && _hasHeading;
            if (!float.IsNaN(yaw) && ManagedIntentBuffer.Finite(yaw) && (headLeads || _settings.YawFollow > 0f))
            {
                float dt = elapsed > 0f ? elapsed : 1f / 60f;
                if (!_yawCaptured) { _startYaw = _filteredYaw = yaw; _yawVelocity = 0f; _yawCaptured = true; }
                _filteredYaw = Mathf.SmoothDampAngle(_filteredYaw, yaw, ref _yawVelocity, YawSmoothTime, float.PositiveInfinity, dt);
                float degrees;
                if (headLeads)
                {
                    // Head toward the dragger = heading opposite their facing. The shape rotates there at a
                    // capped rate so a head-away corpse swings round instead of being flung.
                    float wanted = Mathf.DeltaAngle(_headYaw, _filteredYaw + 180f);
                    _orient = Mathf.MoveTowardsAngle(_orient, wanted, _settings.HeadLeadTurnRate * dt);
                    degrees = _orient;
                }
                else degrees = Mathf.DeltaAngle(_startYaw, _filteredYaw) * _settings.YawFollow;
                float radians = degrees * Mathf.Deg2Rad;
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
