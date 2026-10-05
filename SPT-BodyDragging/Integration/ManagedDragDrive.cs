using System;
using System.Collections.Generic;
using UnityEngine;

namespace BodyDragging.Integration
{
    internal sealed class ManagedDragSettings
    {
        internal float Slack, HandSpeed, TeleportDistance, HoldError, LimbSpring, MaxAcceleration;
        internal bool Valid => Positive(Slack) && Positive(HandSpeed) && Positive(TeleportDistance) && Positive(HoldError) &&
            ManagedIntentBuffer.Finite(LimbSpring) && LimbSpring >= 0 && Positive(MaxAcceleration);
        private static bool Positive(float value) => ManagedIntentBuffer.Finite(value) && value > 0;
        internal static ManagedDragSettings Capture() => new ManagedDragSettings
        {
            Slack = Plugin.HoldSlack.Value, HandSpeed = Plugin.MaxHandSpeed.Value,
            TeleportDistance = Plugin.TeleportDistance.Value, HoldError = Plugin.MaxHoldError.Value,
            LimbSpring = Plugin.GrabSpring.Value * Plugin.LimbFollowStrength.Value,
            MaxAcceleration = Plugin.MaximumGrabAcceleration.Value
        };
    }

    // State comes exclusively from completed Rupture steps. Camera input can be
    // coalesced; recovery stays pending until the ABI acknowledges its id.
    internal sealed class ManagedDragDrive
    {
        private readonly ManagedDragSettings _settings;
        private readonly Vector3[] _initial;
        private readonly Vector3 _initialChest;
        private Vector3 _lastTarget;
        private uint _nextTranslation;
        internal uint TranslationId { get; private set; }
        internal Vector3 Translation { get; private set; }
        internal ManagedDragDrive(ManagedDragSettings settings, ProviderView view)
        {
            _settings = settings;
            _initial = new Vector3[view.Bodies.Length];
            for (int i = 0; i < _initial.Length; i++) _initial[i] = view.Bodies[i].Position;
            _initialChest = view.Bodies[view.GripIndex].Position;
            _lastTarget = view.GripPosition;
        }
        internal ProviderAcceleration[] Compose(ProviderView view, Vector3 target, float elapsed = 0)
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
            if (_settings.LimbSpring == 0) return Array.Empty<ProviderAcceleration>();
            Vector3 delta = chest.Position - _initialChest;
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
                Vector3 value = ((_initial[body.Index] + delta - body.Position) * _settings.LimbSpring -
                    body.Velocity * (damping + _settings.LimbSpring * step)) / denominator;
                forces.Add(new ProviderAcceleration { Index = body.Index, Value = Vector3.ClampMagnitude(value, _settings.MaxAcceleration) });
            }
            return forces.ToArray();
        }
    }
}
