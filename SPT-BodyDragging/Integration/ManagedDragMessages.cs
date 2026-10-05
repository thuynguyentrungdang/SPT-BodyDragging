using System;
using UnityEngine;

namespace BodyDragging.Integration
{
    // These are camera intent and claim state, never a second corpse pose stream.
    public struct ManagedDragStart
    {
        public string Session;
        public string ProfileId;
        public uint DeathSequence;
        public string DraggerProfileId;
    }
    public struct ManagedDragInput
    {
        public string Session;
        public string ProfileId;
        public uint DeathSequence;
        public uint Sequence;
        public bool HasTarget;
        public Vector3 Target;
        // Camera-relative hold, so the host can rebuild the target from the dragger's own
        // synced pose each frame: root + R(yaw) * (LocalX, 0, LocalZ + Distance), y = root.y + HeightOffset.
        public float Distance, LocalX, LocalZ, HeightOffset, Yaw;
    }
    public struct ManagedDragEnd
    {
        public string Session;
        public string ProfileId;
        public uint DeathSequence;
    }
    public enum ManagedDragStage { Preparing, Held, Releasing, Closed, Failed }
    public struct ManagedDragStatus
    {
        public string Session;
        public string ProfileId;
        public uint DeathSequence;
        public uint Sequence;
        public ManagedDragStage Stage;
        public Vector3 GripPoint;
    }

    internal sealed class ManagedIntentBuffer
    {
        internal uint Sequence { get; private set; }
        internal bool HasTarget { get; private set; }
        internal Vector3 Target { get; private set; }
        internal ManagedDragInput Latest { get; private set; }
        internal static bool FiniteHold(ManagedDragInput input) => Finite(input.Distance) && Finite(input.LocalX) &&
            Finite(input.LocalZ) && Finite(input.HeightOffset) && Finite(input.Yaw);
        internal static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        internal static bool ValidSession(string value) => value != null && value.Length == 32 && Guid.TryParseExact(value, "N", out _);
        internal static bool Same(string session, string profile, uint death, string otherSession, string otherProfile, uint otherDeath) =>
            session == otherSession && profile == otherProfile && death == otherDeath;
        internal bool Accept(ManagedDragInput input)
        {
            if (input.Sequence == 0 || input.Sequence <= Sequence || !Finite(input.Target) || !FiniteHold(input)) return false;
            Latest = input;
            Sequence = input.Sequence;
            HasTarget = input.HasTarget;
            Target = input.Target;
            return true;
        }
    }

    internal sealed class ManagedDragLifetime
    {
        internal readonly float Started;
        private float _lastInput;
        internal ManagedDragLifetime(float now) { Started = now; _lastInput = now; }
        internal void Renew(float now) => _lastInput = now;
        internal bool Expired(float now, bool ready) => now - _lastInput >= 2f || (!ready && now - Started >= 6f);
    }
}
