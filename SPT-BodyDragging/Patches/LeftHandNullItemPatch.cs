using System.Reflection;
using EFT.InventoryLogic;
using EFT.ItemInHandSubsystem;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace BodyDragging.Patches
{
    // The drag raises the left arm with LeftHandController.ToggleHand(null) - the pose without an
    // item model. EFT's take-item animation event would then dereference the null item.
    public sealed class LeftHandNullItemPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(LeftHandController), nameof(LeftHandController.CreateItem));

        [PatchPrefix]
        private static bool SkipNullItem(Item item) => item != null;
    }
}
