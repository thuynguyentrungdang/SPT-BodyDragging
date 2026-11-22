using System.Reflection;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;
using BodyDragging.Features;

namespace BodyDragging.Patches
{
    // Armed drag with a non-pistol weapon is hip fire only: the left hand is busy with the body,
    // so ADS is refused for the dragging player (DragHands.BlocksAim lets pistols through).
    // Player.FirearmController is the base of every firearm controller's SetAim override chain.
    public sealed class FirearmSetAimBlockPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(Player.FirearmController), nameof(Player.FirearmController.SetAim), new[] { typeof(bool) });

        [PatchPrefix]
        private static void RefuseAim(Player ____player, ref bool value)
        {
            if (value && DragHands.BlocksAim(____player))
                value = false;
        }
    }

    // ToggleAim drops the left-hand item before it calls SetAim: skipped entirely when aiming is
    // blocked, and for a pistol (ADS stays one-handed) the drop is suppressed while it runs
    // (PlayerRemoveLeftHandGuardPatch).
    public sealed class FirearmToggleAimBlockPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(Player.FirearmController), nameof(Player.FirearmController.ToggleAim));

        [PatchPrefix]
        private static bool RefuseToggle(Player ____player)
        {
            if (DragHands.BlocksAim(____player))
                return false;
            DragHands.SuppressLeftHandRemoval = DragHands.KeepsLeftHandWhileAiming(____player);
            return true;
        }

        [PatchPostfix]
        private static void EndToggle() => DragHands.SuppressLeftHandRemoval = false;
    }
}
