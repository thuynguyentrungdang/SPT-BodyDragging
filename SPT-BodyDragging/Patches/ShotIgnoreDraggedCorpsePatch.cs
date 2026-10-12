using System.Reflection;
using EFT;
using EFT.Ballistics;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;
using BodyDragging.Features;

namespace BodyDragging.Patches
{
    // Armed drag: the held body sits in front of the muzzle on the Deadbody layer, which the
    // ballistics hit mask includes. The local player's own shots pass through the body they drag
    // (a corpse here, a Fika downed ragdoll through the bridge's BodyDragSync hook).
    public sealed class ShotIgnoreDraggedCorpsePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(Shot), nameof(Shot.IsHitIgnored),
                new[] { typeof(IObserverToPlayerBridge), typeof(RaycastHit), typeof(bool) });

        [PatchPostfix]
        private static void IgnoreDraggedBody(IObserverToPlayerBridge player, RaycastHit hit, ref bool __result)
        {
            // runs for every bullet step - static bools first exit nearly all calls
            if (__result || !(CorpseDragController.HasActiveDrag || BodyDragSync.ExternalDragActive) ||
                !ReferenceEquals(player, GamePlayerOwner.MyPlayer))
                return;
            if (CorpseDragController.IsDraggedCollider(hit.collider) ||
                (BodyDragSync.ExternalDragActive && BodyDragSync.IsDraggedDownedCollider?.Invoke(hit.collider) == true))
                __result = true;
        }
    }
}
