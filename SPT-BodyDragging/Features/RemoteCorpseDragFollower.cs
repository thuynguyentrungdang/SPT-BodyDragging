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
    // Peer-side half of the Fika bridge: drives a remote player's drag on a local Corpse /
    // ObservedCorpse by replaying the poses the dragger sends. No Fika type is referenced here -
    // the bridge hands this plain BodyDragPose structs through the BodyDragSync seam, same as it
    // reads CorpseDragController's own local pose.
    internal static class RemoteCorpseDragFollower
    {
        private const float StaleTimeoutSeconds = 1.5f;
        private const float MissRetryIntervalSeconds = 0.5f;

        private sealed class FollowState
        {
            internal Corpse Corpse;
            internal CorpseWeaponLink.DetachedWeapon DetachedWeapon;
            internal float LastPoseTime;
        }

        private static readonly Dictionary<string, FollowState> Active = new();
        // profileIds whose corpse wasn't found locally yet - without this, a pose arriving before
        // the corpse exists (or for a profile that never resolves) would re-run FindObjectsOfType
        // on every packet, up to ~15/sec
        private static readonly Dictionary<string, float> MissRetryAfter = new();

        internal static void ApplyPose(string profileId, BodyDragPose pose)
        {
            FollowState state = GetOrCreate(profileId);
            if (state?.Corpse == null)
                return;
            state.LastPoseTime = Time.unscaledTime;
            ApplyToCorpse(state.Corpse, pose);
        }

        internal static void ApplyStop(string profileId, BodyDragPose pose)
        {
            if (Active.TryGetValue(profileId, out FollowState state) && state.Corpse != null)
                ApplyToCorpse(state.Corpse, pose);
            Release(profileId);
        }

        // periodic staleness sweep, driven by Plugin.Update - a disconnect or dropped DragStop
        // leaves no network message behind, so a follower with no fresh pose just lets go
        internal static void Tick()
        {
            if (Active.Count == 0)
                return;
            float now = Time.unscaledTime;
            List<string> stale = null;
            foreach (KeyValuePair<string, FollowState> entry in Active)
                if (now - entry.Value.LastPoseTime > StaleTimeoutSeconds)
                    (stale ??= new List<string>()).Add(entry.Key);
            if (stale == null)
                return;
            foreach (string profileId in stale)
                Release(profileId);
        }

        internal static void ReleaseAll()
        {
            foreach (string profileId in Active.Keys.ToList())
                Release(profileId);
            MissRetryAfter.Clear();
        }

        private static FollowState GetOrCreate(string profileId)
        {
            if (Active.TryGetValue(profileId, out FollowState existing))
                return existing;

            float now = Time.unscaledTime;
            if (MissRetryAfter.TryGetValue(profileId, out float retryAfter) && now < retryAfter)
                return null;

            Corpse corpse = FindCorpse(profileId);
            if (corpse?.Ragdoll == null)
            {
                MissRetryAfter[profileId] = now + MissRetryIntervalSeconds;
                return null;
            }
            if (RuptureDragProvider.Inspect(corpse, out _) != CorpseRoute.Native) return null;
            // a host relaying/broadcasting its own dragger's pose packets can hand them straight
            // back to that same local client (self-echo) - without this guard, the ~15Hz network
            // pose would fight the local per-frame joint-driven drag on the exact same corpse,
            // producing constant jitter (move forward, snap back to the stale packet, repeat)
            if (CorpseDragController.IsDragging(corpse))
            {
                MissRetryAfter[profileId] = now + MissRetryIntervalSeconds;
                return null;
            }
            MissRetryAfter.Remove(profileId);

            CorpseRagdollSettlement.TakeOver(corpse.Ragdoll);
            CorpseRagdollSettlement.Cancel(corpse);
            FollowState state = new FollowState
            {
                Corpse = corpse,
                DetachedWeapon = CorpseWeaponLink.Detach(corpse.Ragdoll),
                LastPoseTime = now
            };
            Active[profileId] = state;
            BodyDragLog.Info($"[CorpseDrag] Following remote drag for {profileId}");
            return state;
        }

        private static void Release(string profileId)
        {
            if (!Active.TryGetValue(profileId, out FollowState state))
                return;
            Active.Remove(profileId);
            if (state.Corpse == null)
                return;
            if (RuptureDragProvider.Inspect(state.Corpse, out _) == CorpseRoute.Native)
            {
                state.DetachedWeapon?.RestoreCollisions();
                CorpseRagdollSettlement.Schedule(state.Corpse, state.Corpse.Ragdoll);
            }
            BodyDragLog.Info($"[CorpseDrag] Stopped following remote drag for {profileId}");
        }

        private static Corpse FindCorpse(string profileId)
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

        private static void ApplyToCorpse(Corpse corpse, BodyDragPose pose)
        {
            if (RuptureDragProvider.Inspect(corpse, out _) != CorpseRoute.Native) return;
            RigidbodySpawner[] spawners = corpse.Ragdoll?._rigidbodySpawners;
            if (spawners == null || pose.BonePositions == null || pose.BonePositions.Length != spawners.Length)
                return;

            TransformSync[] syncs = new TransformSync[spawners.Length];
            for (int i = 0; i < spawners.Length; i++)
                syncs[i] = new TransformSync { Position = pose.BonePositions[i], Rotation = pose.BoneRotations[i] };
            corpse.ApplyTransformSync(syncs);
        }
    }
}
