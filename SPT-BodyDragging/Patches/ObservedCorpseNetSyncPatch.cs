using System.Reflection;
using EFT.Interactive;
using HarmonyLib;
using SPT.Reflection.Patching;
using BodyDragging.Features;
using UnityEngine;

namespace BodyDragging.Patches
{
    // Whenever the local player is a plain Fika client (not host), every corpse they didn't
    // simulate themselves - any bot or other player's kill - is created as an ObservedCorpse
    // (EFT.Interactive.ObservedPlayer.CreateCorpse), not a plain Corpse. ObservedCorpse.MoveTo
    // is EFT's own native multiplayer corpse-position sync: on every incoming CorpseSyncPacket
    // from whoever actually simulates the corpse (the host - on a headless raid, always the
    // headless process, since the dragging client never does), it nudges the pelvis toward the
    // host's authoritative position and calls Ragdoll.WakeUp() if the gap exceeds
    // CorpseSyncThreshold. That keeps running the whole time we're settling/dragging - nothing in
    // CorpseRagdollSettlement.TakeOver touches it - so a sync packet landing mid-drag re-wakes and
    // repositions bodies out from under our own joint-tether drive, exactly reproducing the
    // explosion our own settle-wait can't see coming or protect against. Explains why this is
    // headless-specific (as a client, you're never the host, so every corpse you drag is an
    // ObservedCorpse) and why waiting didn't help (the next packet can land after the wait, or
    // mid-drag).
    //
    // Suppressed only while we're the one dragging that exact corpse - once released, EFT's own
    // sync resumes normally for everyone else's view of it.
    public sealed class ObservedCorpseNetSyncPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(ObservedCorpse), nameof(ObservedCorpse.MoveTo));

        [PatchPrefix]
        private static bool Prefix(ObservedCorpse __instance, Vector3 position, float corpseSyncThreshold, ref float __result)
        {
            if (!CorpseDragController.IsDragging(__instance))
                return true;
            // "already within threshold" - skip the pelvis shift and the WakeUp() that comes with it
            __result = corpseSyncThreshold;
            return false;
        }
    }
}
