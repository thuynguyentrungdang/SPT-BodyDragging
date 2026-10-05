using System.Reflection;
using EFT;
using EFT.Interactive;
using EFT.UI;
using HarmonyLib;
using SPT.Reflection.Patching;
using BodyDragging.Features;
using BodyDragging.Integration;

namespace BodyDragging.Patches
{
    // Trimmed from TraumaCore's AddCorpseWoundInspectionActionPatch (Apache-2.0, Hysocs): only
    // the DRAG BODY / STOP DRAGGING actions survive, INSPECT WOUNDS and the wound-inspection view
    // are dropped entirely as out of scope for this mod.
    public sealed class AddCorpseDragActionPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(
                typeof(InteractionContextHelper),
                nameof(InteractionContextHelper.GetAvailableActions),
                new[] { typeof(GamePlayerOwner), typeof(LootItem) });

        [PatchPostfix]
        private static void AddDragAction(GamePlayerOwner owner, LootItem lootItem, ref AvailableInteractionState __result)
        {
            if (!Plugin.Enabled.Value || owner == null || !(lootItem is Corpse corpse) || __result == null)
                return;
            if (!Plugin.AllowZombieOrBotCorpses.Value && corpse.IsZombieCorpse)
                return;

            if (CorpseDragController.IsDragging(corpse))
            {
                __result.Actions.Clear();
                __result.Actions.Add(new InteractionAction
                {
                    Name = "STOP DRAGGING",
                    TargetName = lootItem.Name.Localized(),
                    Action = CorpseDragController.StopActiveDrag
                });
                return;
            }

            if (CorpseDragController.HasActiveDrag)
                return;
            if (RuptureDragProvider.Inspect(corpse, out _) == CorpseRoute.Blocked) return;
            if (BodyDragSync.Active && BodyDragSync.IsProfileAlreadyDragged?.Invoke(corpse.PlayerProfileID) == true)
                return;

            __result.Actions.Add(new InteractionAction
            {
                Name = "DRAG BODY",
                TargetName = lootItem.Name.Localized(),
                Action = () => CorpseDragController.Begin(owner, corpse)
            });
        }
    }
}
