using System.Reflection;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;
using BodyDragging.Features;

namespace BodyDragging.Patches
{
    // While a player is armed-dragging, lift the left-arm IK weight so the wrist IK that
    // IkProcess runs next (Player.LeftHandInteractionTarget) lands on the dragged body's chest.
    public sealed class LeftArmGripIkPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(Player), nameof(Player.IkProcess));

        [PatchPrefix]
        private static void LiftWeight(Player __instance)
        {
            if (DragHands.PrepareIk(__instance, out float weight))
                DragHands.LiftLeftArmWeight(__instance, weight);
        }
    }
}
