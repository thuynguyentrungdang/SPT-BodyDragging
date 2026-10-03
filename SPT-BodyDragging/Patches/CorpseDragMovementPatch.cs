using System.Reflection;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;
using BodyDragging.Features;

namespace BodyDragging.Patches
{
    // Trimmed from TraumaCore's BruiseMovementPatch (Apache-2.0, Hysocs): only the corpse-drag
    // speed penalty survives, bruise/TraumaController/spinal-fracture logic is dropped.
    public sealed class CorpseDragMovementPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(MovementContext), nameof(MovementContext.ClampSpeed));

        [PatchPostfix]
        private static void ApplyDragSpeedPenalty(Player ____player, ref float __result)
        {
            // runs for every player incl. bots every frame - static bool first exits nearly all calls
            if (!CorpseDragController.HasActiveDrag || __result <= 0f || ____player == null || !____player.IsYourPlayer)
                return;

            __result *= Plugin.DragSpeedMultiplier.Value;
        }
    }
}
