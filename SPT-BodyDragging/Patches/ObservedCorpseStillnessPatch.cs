using System.Reflection;
using EFT.Interactive;
using HarmonyLib;
using SPT.Reflection.Patching;
using BodyDragging.Features;

namespace BodyDragging.Patches
{
    // Generalizes the same "we claimed this corpse, nobody else gets to touch its ragdoll state"
    // principle behind ObservedCorpseNetSyncPatch to the other way a corpse can get force-frozen
    // out from under the settle-wait: a third-party ObservedCorpseIsStill-style patch (confirmed:
    // a ragdoll-count/perf mod) can make ObservedCorpse.CheckCorpseIsStill report "still" on its
    // own schedule, independent of the corpse's real physical state. EFT's WorkingCycle only reacts
    // to that boolean - it then StopRigidbody()s every body (kinematic) regardless of whether it
    // was genuinely settled or just mid-flail when the mod decided to freeze it. Our velocity-based
    // settle-wait (CorpseDragController.IsRagdollCalm) skips kinematic bodies as trivially calm, so
    // it reads "calm" immediately on a pose that was never actually relaxed - then EngageDrag force-
    // unfreezes it straight into dynamic joint physics, reproducing the original explosion via a
    // third mechanism (after EFT's own WorkingCycle timing, and EFT's native ApplyNetPacket/MoveTo).
    //
    // Fix: force "not still" while we've claimed the corpse (settling or dragging, same window as
    // IsDragging), so nothing - not EFT's own default logic, not any other mod hooking the same
    // method - can freeze it out from under our settle-wait. Runs after whatever other postfixes
    // already ran, so it wins regardless of patch order.
    public sealed class ObservedCorpseStillnessPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(ObservedCorpse), nameof(ObservedCorpse.CheckCorpseIsStill));

        // Harmony runs postfixes in ascending priority order - the highest-priority postfix runs
        // last and has final say over __result. Without this, whether we or the other mod's
        // postfix "wins" would depend on unrelated load order between the two plugins.
        [HarmonyPriority(Priority.First)]
        [PatchPostfix]
        private static void Postfix(ObservedCorpse __instance, ref bool __result)
        {
            if (CorpseDragController.IsDragging(__instance))
                __result = false;
        }
    }
}
