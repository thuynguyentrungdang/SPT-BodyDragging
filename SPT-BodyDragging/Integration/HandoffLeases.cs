using System;
using System.Collections.Generic;
using EFT.Interactive;
using JsonType;
using UnityEngine;
using BodyDragging.Features;

namespace BodyDragging.Integration
{
    internal enum LeaseAcquire { Ready, Pending, Refused }

    // One Rupture handoff token per corpse on this peer (Rupture 1.4.0+, see RuptureHandoffProvider).
    // Owns the token's whole life so every exit path pairs a Release with exactly one Adopt:
    //   Acquire  -> Release at pickup, wait until Rupture reports the corpse Released
    //   Tick     -> Renew every second while held (a lost/expired token drops the lease)
    //   BeginDrop-> the drag ended; settle, apply the final pose, then Adopt (a re-grab inside this
    //               window just calls Acquire again and keeps the same token, as Rupture documents)
    //   Adopt    -> polled until Rupture has rebuilt its rig from the final pose
    // Main thread only (driven from Plugin.Update).
    internal static class HandoffLeases
    {
        // Rupture adopts the corpse by itself when a lease runs out, so a crashed consumer leaves a normal corpse
        private const float LeaseSeconds = 30f;
        private const float RenewInterval = 1f;
        // a preparing rig queues the release until its capture is done
        private const float ReleaseWaitSeconds = 6f;
        private const float AdoptWaitSeconds = 10f;
        private const float AdoptPollSeconds = 0.25f;
        private const float MinSettleSeconds = 0.3f;
        private const float SettleTimeoutSeconds = 3f;
        // how long a peer that did not drop the body waits for the dropper's rest pose; the dropper settles for up to
        // SettleTimeoutSeconds first. A headless host never simulates the hold, so on timeout it would adopt at the
        // pickup pose and every client would follow it back there: it waits longer.
        internal const float GraphicalRestWaitSeconds = 8f;
        internal const float HeadlessRestWaitSeconds = 12f;

        private enum Stage { Releasing, Held, Adopting }

        private sealed class Lease
        {
            internal Corpse Corpse;
            internal ulong Token;
            internal Stage Stage;
            internal float StageAt, NextRenew, NextPoll;
            internal HandoffInfo Info;
            internal bool Dropping, Declarer;
            internal float DropAt, DropDeadline;
        }

        private static readonly List<Lease> Leases = new();
        private static readonly List<Lease> Scratch = new();

        // Engine-facing steps, replaceable so the token life cycle can be exercised without a running game.
        internal static Func<float> Clock = () => Time.unscaledTime;
        internal static Func<Corpse, bool> Settled = CorpseRestDeclarer.IsSettled;
        internal static Action<Corpse, bool, bool> DeclareRest = CorpseRestDeclarer.Declare;
        internal static Action<Corpse> FinishPhysics = corpse => NativeDragBody.FinishHandoffPhysics(corpse?.Ragdoll);
        internal static Action<Corpse, Vector3, TransformSync[]> SetDown =
            (corpse, root, bones) => CorpseRagdollSettlement.SetDown(corpse, root, bones, true);

        private static string Name(Corpse corpse)
        {
            try { return corpse != null ? corpse.PlayerProfileID : "none"; }
            catch (Exception) { return "?"; }
        }

        internal static void Reset() { Leases.Clear(); Scratch.Clear(); }

        private static Lease Find(Corpse corpse)
        {
            if (corpse == null) return null;
            foreach (Lease lease in Leases)
                if (lease.Corpse == corpse) return lease;
            return null;
        }

        // this peer holds (or is acquiring) the corpse for its own physics
        internal static bool Holds(Corpse corpse)
        {
            Lease lease = Find(corpse);
            return lease != null && lease.Stage != Stage.Adopting;
        }

        internal static bool HasLeases => Leases.Count != 0;

        // true while Rupture is still moving the corpse (before pickup); the gate that replaces a velocity
        // check, because after Release the native bodies are kinematic and report nothing
        internal static bool IsSimulating(Corpse corpse)
        {
            if (corpse == null || Find(corpse) != null) return false;
            return RuptureHandoffProvider.Inspect(corpse, out HandoffInfo info) == HandoffCode.Ok &&
                info != null && info.State == HandoffState.Managed && info.Simulating;
        }

        // Idempotent. Ready: the corpse is Released and the info (severed roots, weapon) is valid. Pending: keep calling.
        internal static LeaseAcquire Acquire(Corpse corpse, out HandoffInfo info)
        {
            info = null;
            if (corpse == null || !RuptureHandoffProvider.Available) return LeaseAcquire.Refused;
            Lease lease = Find(corpse);
            if (lease != null)
            {
                // a closing token cannot be reused; a fresh Release follows once it is gone
                if (lease.Stage == Stage.Adopting) return LeaseAcquire.Pending;
                lease.Dropping = false;
                info = lease.Info;
                return lease.Stage == Stage.Held ? LeaseAcquire.Ready : LeaseAcquire.Pending;
            }

            HandoffCode inspect = RuptureHandoffProvider.Inspect(corpse, out HandoffInfo state);
            if (inspect != HandoffCode.Ok || state == null || state.State != HandoffState.Managed)
            {
                BodyDragLog.Info($"[Handoff] Not taking {Name(corpse)}: inspect {inspect}, state {state?.State.ToString() ?? "?"}");
                return LeaseAcquire.Refused;
            }
            HandoffCode result = RuptureHandoffProvider.Release(corpse, LeaseSeconds, out ulong token);
            if ((result != HandoffCode.Pending && result != HandoffCode.Ok) || token == 0)
            {
                BodyDragLog.Info($"[Handoff] Release of {Name(corpse)} refused: {result}");
                return LeaseAcquire.Refused;
            }
            float now = Clock();
            Leases.Add(new Lease { Corpse = corpse, Token = token, Stage = Stage.Releasing, StageAt = now, NextRenew = now + RenewInterval, Info = state });
            BodyDragLog.Info($"[Handoff] Released {Name(corpse)} to BodyDragging (token {token})");
            return LeaseAcquire.Pending;
        }

        // The drag on this peer ended. declarer: this peer dropped the body, so it settles and tells the others where it
        // lies. Otherwise it waits for that rest pose (or falls back to its own pose). Either way ends in one Adopt.
        internal static void BeginDrop(Corpse corpse, bool declarer, float waitSeconds)
        {
            Lease lease = Find(corpse);
            if (lease == null || lease.Stage == Stage.Adopting) return;
            float now = Clock();
            lease.Dropping = true;
            lease.Declarer = declarer;
            lease.DropAt = now;
            lease.DropDeadline = now + waitSeconds;
        }

        // The dropper's final pose arrived. False when this peer is not holding the corpse for it.
        internal static bool ApplyRest(Corpse corpse, Vector3 root, TransformSync[] bones)
        {
            Lease lease = Find(corpse);
            if (lease == null || lease.Stage != Stage.Held) return false;
            SetDown(corpse, root, bones);
            Retire(lease, Clock());
            return true;
        }

        internal static void Tick()
        {
            if (Leases.Count == 0) return;
            float now = Clock();
            Scratch.Clear();
            Scratch.AddRange(Leases);
            foreach (Lease lease in Scratch)
            {
                if (!Leases.Contains(lease)) continue;
                if (lease.Corpse == null)
                {
                    // Rupture disposes its side of a destroyed corpse's token by itself
                    Leases.Remove(lease);
                    continue;
                }
                switch (lease.Stage)
                {
                    case Stage.Releasing: StepReleasing(lease, now); break;
                    case Stage.Held: StepHeld(lease, now); break;
                    default: StepAdopting(lease, now); break;
                }
            }
            Scratch.Clear();
        }

        private static void StepReleasing(Lease lease, float now)
        {
            HandoffCode code = RuptureHandoffProvider.Inspect(lease.Corpse, out HandoffInfo info);
            if (code == HandoffCode.Ok && info != null && info.State == HandoffState.Released)
            {
                lease.Info = info;
                lease.Stage = Stage.Held;
                lease.StageAt = now;
                BodyDragLog.Info($"[Handoff] {Name(lease.Corpse)} released: severed roots {info.SeveredRoots.Length}, weapon independent {info.WeaponIndependent}");
                return;
            }
            bool failed = code != HandoffCode.Ok || info == null ||
                info.State == HandoffState.Unmanaged || info.State == HandoffState.Unavailable;
            if (failed || now - lease.StageAt > ReleaseWaitSeconds)
            {
                BodyDragLog.Warning($"[Handoff] Release of {Name(lease.Corpse)} did not complete ({code}, {info?.State.ToString() ?? "?"}), closing token");
                RuptureHandoffProvider.Adopt(lease.Token);
                Leases.Remove(lease);
            }
        }

        private static void StepHeld(Lease lease, float now)
        {
            if (now >= lease.NextRenew)
            {
                lease.NextRenew = now + RenewInterval;
                HandoffCode renewed = RuptureHandoffProvider.Renew(lease.Token);
                if (renewed != HandoffCode.Ok)
                {
                    // Stale/Failed/InvalidToken: Rupture already adopted (expiry) or the raid reset; nothing left to close
                    BodyDragLog.Warning($"[Handoff] Lease on {Name(lease.Corpse)} lost ({renewed})");
                    Leases.Remove(lease);
                    return;
                }
            }
            if (!lease.Dropping) return;

            if (lease.Declarer)
            {
                float elapsed = now - lease.DropAt;
                if (elapsed < MinSettleSeconds || (!Settled(lease.Corpse) && elapsed < SettleTimeoutSeconds)) return;
                DeclareRest(lease.Corpse, BodyDragSync.Active, true);
                Retire(lease, now);
                return;
            }
            if (now < lease.DropDeadline) return;
            if (!Settled(lease.Corpse) && now < lease.DropDeadline + SettleTimeoutSeconds) return;
            BodyDragLog.Info($"[Handoff] No rest pose for {Name(lease.Corpse)}, adopting at this peer's own pose");
            DeclareRest(lease.Corpse, false, true);
            Retire(lease, now);
        }

        // Back to Rupture: our bodies go back to the kinematic, unsupported shell it expects, then one Adopt.
        private static void Retire(Lease lease, float now)
        {
            lease.Dropping = false;
            if (lease.Corpse != null) FinishPhysics(lease.Corpse);
            HandoffCode code = RuptureHandoffProvider.Adopt(lease.Token);
            switch (code)
            {
                case HandoffCode.Pending:
                    lease.Stage = Stage.Adopting;
                    lease.StageAt = now;
                    lease.NextPoll = now + AdoptPollSeconds;
                    break;
                case HandoffCode.Ok:
                    BodyDragLog.Info($"[Handoff] {Name(lease.Corpse)} adopted");
                    Leases.Remove(lease);
                    break;
                default:
                    // Stale/InvalidToken: already adopted by expiry or raid reset. Failed: Rupture keeps the corpse native.
                    BodyDragLog.Info($"[Handoff] Adopt of {Name(lease.Corpse)} returned {code}");
                    Leases.Remove(lease);
                    break;
            }
        }

        private static void StepAdopting(Lease lease, float now)
        {
            if (now < lease.NextPoll) return;
            lease.NextPoll = now + AdoptPollSeconds;
            HandoffCode code = RuptureHandoffProvider.Adopt(lease.Token);
            if (code == HandoffCode.Pending && now - lease.StageAt <= AdoptWaitSeconds) return;
            if (code == HandoffCode.Ok) BodyDragLog.Info($"[Handoff] {Name(lease.Corpse)} adopted");
            else BodyDragLog.Warning($"[Handoff] Adopt of {Name(lease.Corpse)} ended with {code}");
            Leases.Remove(lease);
        }

        // Raid end / shutdown: give every held corpse back, best effort.
        internal static void AdoptAll()
        {
            Scratch.Clear();
            Scratch.AddRange(Leases);
            foreach (Lease lease in Scratch)
            {
                if (lease.Stage == Stage.Adopting) continue;
                if (lease.Corpse != null) FinishPhysics(lease.Corpse);
                RuptureHandoffProvider.Adopt(lease.Token);
            }
            Scratch.Clear();
            Leases.Clear();
        }
    }
}
