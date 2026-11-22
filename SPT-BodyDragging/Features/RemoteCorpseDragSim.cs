using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using JsonType;
using UnityEngine;
using BodyDragging.Integration;

namespace BodyDragging.Features
{
    // Every other peer's half of a native drag. No poses are streamed: this peer takes the corpse's
    // ragdoll over and simulates its own copy (NativeDragBody), held by the dragger's hand placed
    // from what this peer already has - the dragger's synced position - plus the hold parameters
    // the dragger sends (yaw, distance, camera offset). When the dragger lets go, the peer that
    // dropped it sends the settled pose once (BodyDragRest) and every copy is set down on it.
    // A headless host has no player to hold anything, so it only applies the rest.
    // No Fika type is referenced here - the bridge feeds this through the BodyDragSync seam.
    internal static class RemoteCorpseDragSim
    {
        // no hold packet this long after the first one: the dragger is gone (a lost stop, a disconnect)
        private const float HoldStaleSeconds = 1.5f;
        // before the first hold the dragger may still be waiting for its own corpse to settle
        private const float FirstHoldSeconds = 6f;
        private const float ResolveRetrySeconds = 0.5f;
        private const float EngageBlendSeconds = 0.25f;
        private const float YawSmoothSeconds = 0.1f;

        private enum Phase
        {
            Resolving,
            Settling,
            Engaged,
            // headless handoff: no human to hold the body, but the corpse still has to be released to this peer and
            // handed back, or Rupture's authority would keep the pickup pose and every client would snap to it
            Leasing,
            Failed
        }

        private sealed class SimState
        {
            internal string CorpseId;
            internal string DraggerId;
            internal Corpse Corpse;
            internal NativeDragBody Body;
            internal Phase Phase;
            internal float StartedAt;
            internal float NextResolve;
            internal float SettleElapsed;
            internal float CalmElapsed;
            internal float EngagedAt;
            internal Vector3 StartPoint;
            internal bool Handoff;
            internal bool HasHold;
            internal BodyDragHold Hold;
            internal float LastHoldTime;
            internal float Yaw;
            internal float YawVelocity;
        }

        private static readonly Dictionary<string, SimState> Active = new();
        private static readonly HashSet<Corpse> Simulated = new();
        private static readonly List<string> Finished = new();

        // true from the moment this peer claims the corpse's ragdoll until it lets go (patches that
        // keep EFT's own corpse sync/freeze off a body in use)
        // (a handed-off corpse counts until it is adopted back, including while it waits for the final pose)
        internal static bool IsSimulating(Corpse corpse) =>
            (Simulated.Count != 0 && Simulated.Contains(corpse)) || (HandoffLeases.HasLeases && HandoffLeases.Holds(corpse));

        internal static void ApplyStart(string corpseId, string draggerId)
        {
            if (string.IsNullOrEmpty(corpseId) || string.IsNullOrEmpty(draggerId))
                return;
            float now = Time.unscaledTime;
            if (Active.TryGetValue(corpseId, out SimState existing))
            {
                existing.DraggerId = draggerId;
                existing.LastHoldTime = now;
                return;
            }
            Active[corpseId] = new SimState
            {
                CorpseId = corpseId,
                DraggerId = draggerId,
                Phase = Phase.Resolving,
                StartedAt = now,
                LastHoldTime = now
            };
        }

        internal static void ApplyHold(BodyDragHold hold)
        {
            if (!Active.TryGetValue(hold.ProfileId ?? string.Empty, out SimState state) ||
                !Finite(hold.Yaw) || !Finite(hold.Distance) || !Finite(hold.LocalX) || !Finite(hold.LocalZ) || !Finite(hold.HeightOffset))
                return;
            // Sequenced delivery is ordered; this only drops a stale duplicate
            if (state.HasHold && (int)(hold.Sequence - state.Hold.Sequence) <= 0)
                return;
            if (!state.HasHold)
                state.Yaw = hold.Yaw;
            state.Hold = hold;
            state.HasHold = true;
            state.LastHoldTime = Time.unscaledTime;
        }

        internal static void ApplyStop(string corpseId)
        {
            if (corpseId != null && Active.TryGetValue(corpseId, out SimState state))
                Finish(state);
        }

        // Where the dragger's copy came to rest. Every other copy is put there once nobody has the body.
        internal static void ApplyRest(BodyDragRest rest)
        {
            if (string.IsNullOrEmpty(rest.ProfileId) || Active.ContainsKey(rest.ProfileId) || !Finite(rest.Root) ||
                rest.BonePositions == null || rest.BoneRotations == null || rest.BonePositions.Length != rest.BoneRotations.Length)
                return;
            Corpse corpse = FindCorpse(rest.ProfileId);
            RigidbodySpawner[] spawners = corpse?.Ragdoll?._rigidbodySpawners;
            if (spawners == null || spawners.Length != rest.BonePositions.Length || CorpseDragController.IsDragging(corpse))
                return;
            // a handed-off corpse is set down by its lease, which then adopts it; a Rupture-managed corpse this peer
            // never took follows Rupture's own stream instead
            bool leased = HandoffLeases.Holds(corpse);
            if (!leased && RuptureDragProvider.Inspect(corpse, out _, true) != CorpseRoute.Native)
                return;
            TransformSync[] bones = new TransformSync[spawners.Length];
            for (int i = 0; i < bones.Length; i++)
            {
                if (!Finite(rest.BonePositions[i]) || !Finite(rest.BoneRotations[i]))
                    return;
                bones[i] = new TransformSync { Position = rest.BonePositions[i], Rotation = rest.BoneRotations[i] };
            }
            if (leased)
            {
                if (!HandoffLeases.ApplyRest(corpse, rest.Root, bones))
                    return;
            }
            else
                CorpseRagdollSettlement.SetDown(corpse, rest.Root, bones);
            BodyDragLog.Info($"[CorpseDrag] Set {rest.ProfileId} down where the dragger left it");
        }

        // Driven by Plugin.Update.
        internal static void Tick()
        {
            if (Active.Count == 0)
                return;
            float now = Time.unscaledTime;
            float deltaTime = Time.deltaTime;
            Finished.Clear();
            foreach (KeyValuePair<string, SimState> entry in Active)
            {
                SimState state = entry.Value;
                float limit = state.HasHold ? HoldStaleSeconds : FirstHoldSeconds;
                if (now - state.LastHoldTime > limit || Advance(state, now, deltaTime))
                    Finished.Add(entry.Key);
            }
            foreach (string corpseId in Finished)
                if (Active.TryGetValue(corpseId, out SimState state))
                    Finish(state);
            Finished.Clear();
        }

        internal static void ReleaseAll()
        {
            foreach (SimState state in Active.Values.ToList())
                Finish(state);
        }

        // returns true when this peer should let go (corpse or dragger gone)
        private static bool Advance(SimState state, float now, float deltaTime)
        {
            switch (state.Phase)
            {
                case Phase.Resolving:
                    return Resolve(state, now);
                case Phase.Settling:
                    return Settle(state, deltaTime, now);
                case Phase.Leasing:
                    return Lease(state);
                case Phase.Engaged:
                    return Hold(state, now, deltaTime);
                default:
                    return false;
            }
        }

        private static bool Resolve(SimState state, float now)
        {
            if (now < state.NextResolve)
                return false;
            state.NextResolve = now + ResolveRetrySeconds;
            Corpse corpse = FindCorpse(state.CorpseId);
            if (corpse?.Ragdoll == null)
                return false;
            // own drag (self-echo), a corpse Rupture keeps (it publishes that motion; no handoff offered), a headless
            // process on a plain native corpse (no human to hold the body: it only applies the rest) or a body with no rig
            CorpseRoute route = RuptureDragProvider.Inspect(corpse, out _, true);
            bool headless = GamePlayerOwner.MyPlayer == null;
            if (CorpseDragController.IsDragging(corpse) || (route != CorpseRoute.Native && route != CorpseRoute.Handoff) ||
                (headless && route != CorpseRoute.Handoff) ||
                corpse.Ragdoll._rigidbodySpawners == null || corpse.Ragdoll._rigidbodySpawners.Length == 0)
            {
                state.Phase = Phase.Failed;
                return false;
            }
            state.Corpse = corpse;
            state.Handoff = route == CorpseRoute.Handoff;
            CorpseRagdollSettlement.Cancel(corpse);
            CorpseRestDeclarer.Cancel(corpse);
            if (headless)
            {
                state.Phase = Phase.Leasing;
                BodyDragLog.Info($"[CorpseDrag] Headless: holding {state.CorpseId} for Rupture handoff while {state.DraggerId} drags it");
                return false;
            }
            state.Body = new NativeDragBody(corpse, state.Handoff);
            Simulated.Add(corpse);
            state.Phase = Phase.Settling;
            BodyDragLog.Info($"[CorpseDrag] Simulating remote drag of {state.CorpseId} (held by {state.DraggerId})");
            return false;
        }

        private static bool Settle(SimState state, float deltaTime, float now)
        {
            if (state.Corpse == null || state.Corpse.Ragdoll?._rigidbodySpawners == null)
                return true;
            state.SettleElapsed += deltaTime;
            bool calm = state.Body.IsCalm();
            state.CalmElapsed = calm ? state.CalmElapsed + deltaTime : 0f;
            if (!state.Body.ReadyToEngage(calm, state.CalmElapsed, state.SettleElapsed))
                return false;
            NativeDragBody.EngageResult result = state.Body.TryEngage();
            if (result == NativeDragBody.EngageResult.Pending)
                return false;
            if (result == NativeDragBody.EngageResult.Failed)
            {
                BodyDragLog.Warning($"[CorpseDrag] Could not simulate remote drag of {state.CorpseId}");
                state.Phase = Phase.Failed;
                state.Body.Release();
                Simulated.Remove(state.Corpse);
                // a corpse already released to this peer still has to wait for the dragger's pose and go back
                if (state.Handoff)
                    HandoffLeases.BeginDrop(state.Corpse, false, RestWaitSeconds());
                return false;
            }
            state.StartPoint = state.Body.ChestCenter;
            state.EngagedAt = now;
            state.Phase = Phase.Engaged;
            return false;
        }

        private static bool Hold(SimState state, float now, float deltaTime)
        {
            if (state.Corpse == null || !state.Body.IsEngaged)
                return true;
            if (state.Handoff && !HandoffLeases.Holds(state.Corpse))
                return true;
            Player dragger = Singleton<GameWorld>.Instantiated
                ? Singleton<GameWorld>.Instance.GetEverExistedPlayerByID(state.DraggerId)
                : null;
            if (dragger == null || dragger.HealthController == null || !dragger.HealthController.IsAlive)
                return true;

            Vector3 target = state.Body.ChestCenter;
            if (state.HasHold)
            {
                state.Yaw = Mathf.SmoothDampAngle(state.Yaw, state.Hold.Yaw, ref state.YawVelocity, YawSmoothSeconds, float.PositiveInfinity, deltaTime);
                Vector3 held = RemoteHoldMath.Target(dragger.Position, state.Yaw, state.Hold.LocalX, state.Hold.LocalZ,
                    state.Hold.Distance, state.Hold.HeightOffset);
                float blend = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((now - state.EngagedAt) / EngageBlendSeconds));
                target = Vector3.Lerp(state.StartPoint, held, blend);
            }
            state.Body.Tick(target, deltaTime);
            return false;
        }

        private static void Finish(SimState state)
        {
            Active.Remove(state.CorpseId);
            if (state.Corpse != null)
                Simulated.Remove(state.Corpse);
            if (state.Body != null && state.Phase != Phase.Failed)
                state.Body.Release();
            // handoff: wait for the dragger's final pose (or fall back to this peer's own), then give the corpse back
            if (state.Handoff && state.Corpse != null && state.Phase != Phase.Failed)
                HandoffLeases.BeginDrop(state.Corpse, false, RestWaitSeconds());
            if (state.Phase == Phase.Engaged || state.Phase == Phase.Settling || state.Phase == Phase.Leasing)
                BodyDragLog.Info($"[CorpseDrag] Stopped simulating remote drag of {state.CorpseId}");
        }

        private static float RestWaitSeconds() =>
            GamePlayerOwner.MyPlayer == null ? HandoffLeases.HeadlessRestWaitSeconds : HandoffLeases.GraphicalRestWaitSeconds;

        // headless: keep the corpse acquired (idempotent; also cancels a drop left over from the previous drag)
        private static bool Lease(SimState state)
        {
            if (state.Corpse == null)
                return true;
            if (HandoffLeases.Acquire(state.Corpse, out _) == LeaseAcquire.Refused)
                state.Phase = Phase.Failed;
            return false;
        }

        internal static Corpse FindCorpse(string profileId)
        {
            if (Singleton<GameWorld>.Instantiated)
            {
                GameWorld world = Singleton<GameWorld>.Instance;
                foreach (ObservedCorpse observed in world.ObservedPlayersCorpses.Values)
                    if (observed != null && observed.PlayerProfileID == profileId)
                        return observed;
            }
            // host's own real Corpse objects (and any ObservedCorpse not yet indexed) aren't in
            // ObservedPlayersCorpses by profile id, so fall back to a hierarchy scan
            foreach (Corpse corpse in Object.FindObjectsOfType<Corpse>())
                if (corpse != null && corpse.PlayerProfileID == profileId)
                    return corpse;
            return null;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        private static bool Finite(Quaternion value) => Finite(value.x) && Finite(value.y) && Finite(value.z) && Finite(value.w);
    }
}
