using System.Reflection;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;
using BodyDragging.Features;

namespace BodyDragging.Patches
{
    // Only while the dragging player's ToggleAim runs (DragHands.SuppressLeftHandRemoval): reloads,
    // door interactions and every other caller still drop the left-hand item as usual.
    public sealed class PlayerRemoveLeftHandGuardPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(Player), nameof(Player.RemoveLeftHandItem));

        [PatchPrefix]
        private static bool KeepLeftHand(Player __instance) =>
            !(DragHands.SuppressLeftHandRemoval && ReferenceEquals(__instance, GamePlayerOwner.MyPlayer));
    }
}
