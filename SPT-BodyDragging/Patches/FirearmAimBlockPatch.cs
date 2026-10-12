using System.Reflection;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;
using BodyDragging.Features;

namespace BodyDragging.Patches
{
    // Armed drag is hip fire only: the left hand is busy with the body, so ADS is refused
    // outright for the dragging player (Player.FirearmController is the base of every
    // firearm controller's SetAim override chain).
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

    // ToggleAim drops the left-hand item before it calls SetAim; skip it entirely so the pose stays.
    public sealed class FirearmToggleAimBlockPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(Player.FirearmController), nameof(Player.FirearmController.ToggleAim));

        [PatchPrefix]
        private static bool RefuseToggle(Player ____player) => !DragHands.BlocksAim(____player);
    }
}
