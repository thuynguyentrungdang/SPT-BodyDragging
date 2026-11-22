using System.Reflection;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;
using BodyDragging.Features;

namespace BodyDragging.Patches
{
    // The dragging player can't reload: the left hand is on the body. Every reload entry point
    // (reload key, quick reload, ammo drag-and-drop, per-round loading) asks CanStartReload first,
    // and the SP and Fika client controllers both call down to this base implementation.
    public sealed class FirearmReloadBlockPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(Player.FirearmController), nameof(Player.FirearmController.CanStartReload));

        [PatchPostfix]
        private static void RefuseReload(Player ____player, ref bool __result)
        {
            if (__result && DragHands.BlocksReload(____player))
                __result = false;
        }
    }
}
