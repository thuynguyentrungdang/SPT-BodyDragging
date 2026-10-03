using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using EFT.AssetsManager;
using EFT.Interactive;
using UnityEngine;

namespace BodyDragging.Features
{
    // Ported from TraumaCore's CorpseRagdollSettlement (Apache-2.0, Hysocs). Settles a dropped
    // corpse after a short delay instead of forcing it to stop immediately, so it doesn't look
    // like it teleports to rest. Also owns taking a ragdoll over from EFT's death cycle.
    internal static class CorpseRagdollSettlement
    {
        private const float HandoffDelaySeconds = 1f;
        private const float MaximumSettlementSeconds = 8f;
        private const float SettlementSpeed = 0.08f;

        private sealed class PendingSettlement
        {
            internal MonoBehaviour Owner;
            internal Coroutine Coroutine;
        }

        private static readonly Dictionary<Corpse, PendingSettlement> PendingByCorpse = new();
        // EFT's death coroutine (CorpseRagdoll.WorkingCycle) keeps running under a corpse grabbed
        // before it settled: every frame it Sleep()s low-energy bodies, and once all sleep (or 15s
        // pass) it StopRigidbody()s them - kinematic + unsupported from EFT's manual physics
        // stepping - then Remove()s joints/rigidbodies once off-screen. All mid-drag.
        // The coroutine re-reads these three fields every iteration, so swapping them idles it
        // harmlessly while we own the ragdoll; restoring them hands it back to finish EFT's own
        // stop/cleanup. (Patching WorkingCycle itself doesn't work: it's a tiny iterator stub the
        // Mono JIT inlines into Start(), bypassing the Harmony detour.)
        private sealed class TakenOverState
        {
            internal Func<bool, float, bool> CheckCorpseIsStill;
            internal List<PlayerRigidbodySleepHierarchy> SleepHierarchy;
            internal Func<bool> IsVisibleTest;
        }

        private static readonly ConditionalWeakTable<CorpseRagdoll, TakenOverState> TakenOver = new();
        private static readonly List<PlayerRigidbodySleepHierarchy> NoSleepHierarchy = new();
        private static readonly Func<bool, float, bool> NeverStill = (_, _) => false;
        private static readonly Func<bool> AlwaysVisible = () => true;

        // Call after any CorpseRagdoll.WakeUp() - WakeUp iterates the sleep hierarchy this swaps out
        internal static void TakeOver(CorpseRagdoll ragdoll)
        {
            if (ragdoll == null || TakenOver.TryGetValue(ragdoll, out _))
                return;
            TakenOver.Add(ragdoll, new TakenOverState
            {
                CheckCorpseIsStill = ragdoll._checkCorpseIsStill,
                SleepHierarchy = ragdoll._rigidbodySleepHierarchy,
                IsVisibleTest = ragdoll._isVisibleTest
            });
            ragdoll._checkCorpseIsStill = NeverStill;
            ragdoll._rigidbodySleepHierarchy = NoSleepHierarchy;
            ragdoll._isVisibleTest = AlwaysVisible;
        }

        private static void HandBack(CorpseRagdoll ragdoll)
        {
            if (ragdoll == null || !TakenOver.TryGetValue(ragdoll, out TakenOverState state))
                return;
            TakenOver.Remove(ragdoll);
            ragdoll._checkCorpseIsStill = state.CheckCorpseIsStill;
            ragdoll._rigidbodySleepHierarchy = state.SleepHierarchy;
            ragdoll._isVisibleTest = state.IsVisibleTest;
        }

        internal static void Cancel(Corpse corpse)
        {
            if (corpse == null || !PendingByCorpse.TryGetValue(corpse, out PendingSettlement pending))
                return;
            if (pending.Owner != null && pending.Coroutine != null)
                pending.Owner.StopCoroutine(pending.Coroutine);
            PendingByCorpse.Remove(corpse);
            BodyDragLog.Info("[CorpseDrag] Cancelled previous settling handoff");
        }

        internal static void Schedule(Corpse corpse, CorpseRagdoll ragdoll)
        {
            Cancel(corpse);
            if (corpse == null || ragdoll == null || ragdoll._owner == null)
            {
                HandBack(ragdoll);
                return;
            }

            ragdoll._putToSleep = true;
            PendingSettlement pending = new PendingSettlement { Owner = ragdoll._owner };
            pending.Coroutine = ragdoll._owner.StartCoroutine(SettleAfterHandoff(corpse, ragdoll));
            PendingByCorpse.Add(corpse, pending);
        }

        private static IEnumerator SettleAfterHandoff(Corpse corpse, CorpseRagdoll ragdoll)
        {
            yield return new WaitForSeconds(HandoffDelaySeconds);
            float settlementDuration = 0f;
            while (settlementDuration < MaximumSettlementSeconds && HasMovingBody(ragdoll))
            {
                settlementDuration += Time.deltaTime;
                yield return null;
            }

            PendingByCorpse.Remove(corpse);
            if (ragdoll != null && !ragdoll._isPhysicsDone)
                CompleteSettlement(ragdoll);
            HandBack(ragdoll);
        }

        private static void CompleteSettlement(CorpseRagdoll ragdoll)
        {
            if (HasCompleteBodySet(ragdoll))
            {
                try
                {
                    ragdoll.ForceStopRigidBody();
                    return;
                }
                catch (NullReferenceException exception)
                {
                    BodyDragLog.Warning(
                        "[CorpseDrag] EFT could not stop the complete ragdoll; settling its available bodies instead: " +
                        exception.Message);
                }
            }
            else
            {
                BodyDragLog.Warning(
                    "[CorpseDrag] Ragdoll lost one or more rigidbodies; settling the remaining bodies without EFT's bulk stop");
            }

            StopAvailableBodies(ragdoll);
        }

        private static bool HasCompleteBodySet(CorpseRagdoll ragdoll)
        {
            if (ragdoll?._rigidbodySpawners == null || ragdoll._rigidbodySpawners.Length == 0)
                return false;

            foreach (RigidbodySpawner spawner in ragdoll._rigidbodySpawners)
                if (spawner == null || spawner.Rigidbody == null)
                    return false;

            return true;
        }

        private static void StopAvailableBodies(CorpseRagdoll ragdoll)
        {
            if (ragdoll?._rigidbodySpawners != null)
                foreach (RigidbodySpawner spawner in ragdoll._rigidbodySpawners)
                {
                    Rigidbody body = spawner?.Rigidbody;
                    if (body == null)
                        continue;
                    body.velocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
                    body.isKinematic = true;
                    body.Sleep();
                    // mirror CorpseRagdoll.StopRigidbody: stop EFT stepping physics for it
                    PhysicsExtensions.UpdateController.UnsupportRigidbody(body);
                }

            ragdoll._putToSleep = true;
            ragdoll._isPhysicsDone = true;
        }

        private static bool HasMovingBody(CorpseRagdoll ragdoll)
        {
            if (ragdoll?._rigidbodySpawners == null)
                return false;

            float maximumSpeedSquared = SettlementSpeed * SettlementSpeed;
            foreach (RigidbodySpawner spawner in ragdoll._rigidbodySpawners)
            {
                Rigidbody body = spawner?.Rigidbody;
                if (body != null && (body.velocity.sqrMagnitude > maximumSpeedSquared ||
                    body.angularVelocity.sqrMagnitude > maximumSpeedSquared))
                    return true;
            }
            return false;
        }
    }
}
