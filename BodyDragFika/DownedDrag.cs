using System;
using System.Collections.Generic;
using System.Reflection;
using BodyDragging;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using EFT.UI;
using Fika.Core.Main.Players;
using Fika.Core.Networking;
using Fika.Core.Networking.LiteNetLib;
using Fika.Core.Networking.LiteNetLib.Utils;
using HarmonyLib;
using UnityEngine;

namespace BodyDragFika
{
    // Pure math of the downed drag, ported from KeepMeAlive's DownedDragController (MIT).
    public static class DownedDragMath
    {
        public const float TrailDistance = 1.2f;
        public const float FollowGain = 12f;
        public const float MaxCatchUpSpeed = 14f;
        public const float FollowDeadZone = 0.03f;
        public const float GroundStickSpeed = 2f;
        public const float RemotePullOffset = 0.5f;

        // The body trails behind the dragger without being pushed when they step closer.
        public static Vector3 TrailPoint(Vector3 dragger, Vector3 body)
        {
            Vector3 toBody = body - dragger;
            toBody.y = 0f;
            float distance = toBody.magnitude;
            if (distance <= TrailDistance || distance < 1e-3f)
                return body;
            Vector3 held = dragger + toBody / distance * TrailDistance;
            held.y = body.y;
            return held;
        }

        // Frame-rate independent proportional catch-up with a speed cap and a dead zone (horizontal).
        public static Vector3 FollowMotion(Vector3 trail, Vector3 body, float deltaTime)
        {
            Vector3 gap = trail - body;
            gap.y = 0f;
            float distance = gap.magnitude;
            if (distance <= FollowDeadZone)
                return Vector3.zero;
            float step = Mathf.Min(distance * Mathf.Min(1f, FollowGain * deltaTime), MaxCatchUpSpeed * deltaTime);
            return gap / distance * step;
        }

        // Where observers hold the ragdoll's chest: from the synced root toward the dragger.
        public static Vector3 RemoteHold(Vector3 root, Vector3 dragger)
        {
            Vector3 toDragger = dragger - root;
            toDragger.y = 0f;
            float distance = toDragger.magnitude;
            return distance > 1e-3f ? root + toDragger / distance * Mathf.Min(RemotePullOffset, distance) : root;
        }
    }

    public struct DownedDragPacket : INetSerializable
    {
        public const byte Start = 1;
        public const byte Stop = 2;
        public byte Op;
        public string Downed;
        public string Dragger;
        public void Serialize(NetDataWriter writer)
        {
            writer.Put(Op); writer.Put(Downed ?? ""); writer.Put(Dragger ?? "");
        }
        public void Deserialize(NetDataReader reader)
        {
            Op = reader.GetByte(); Downed = reader.GetString(128); Dragger = reader.GetString(128);
        }
    }

    // Dragging a Fika-downed teammate (KeepMeAlive model, working with Fika's own revive):
    //  - the dragger broadcasts a claim (Start, re-sent as a heartbeat; Stop on release),
    //  - the downed player's own client walks its real position toward a point behind the dragger
    //    (Fika's normal state sync carries it, so revive/loot/AI/corpse all use the dragged position),
    //  - every other peer pulls its local copy of Fika's downed ragdoll toward that hold point.
    public static partial class BodyDragFikaBridge
    {
        private const float DownedHeartbeatSeconds = 3f;
        private const float DownedExpirySeconds = 9f;
        private const float DownedLeashDistance = 7f;

        private sealed class DownedClaim
        {
            internal string Dragger;
            internal float LastSeen;
            internal DownedRagdollDrive Drive;
            internal bool NoDrive;
        }

        private static readonly Dictionary<string, DownedClaim> DownedClaims = new();
        private static readonly List<string> DownedScratch = new();
        private static string _draggingDowned;
        private static Item _previousHandsItem;
        private static float _nextDownedBeat;
        private static Harmony _downedHarmony;
        private static Type _reviveType;
        private static FieldInfo _reviveRagdollField;

        // ---- lifecycle ------------------------------------------------------------------------
        private static void InitializeDowned()
        {
            try
            {
                _reviveType = typeof(ObservedPlayer).Assembly.GetType("Fika.Core.Main.Components.ReviveInteractable");
                _reviveRagdollField = _reviveType?.GetField("_ragdoll", BindingFlags.Instance | BindingFlags.NonPublic);
                if (_reviveType == null || _reviveRagdollField == null)
                {
                    _log?.LogWarning("[BodyDragFika] Fika's ReviveInteractable not found - downed-player dragging disabled");
                    return;
                }

                _downedHarmony = new Harmony("com.kobethuy.bodydragging.downed");
                _downedHarmony.Patch(AccessTools.Method(typeof(InteractionContextHelper), "GetAvailableActions",
                        new[] { typeof(GamePlayerOwner), typeof(IInteractive) }),
                    prefix: new HarmonyMethod(typeof(BodyDragFikaBridge), nameof(DownedDragMenuPrefix)),
                    postfix: new HarmonyMethod(typeof(BodyDragFikaBridge), nameof(DownedMenuPostfix)));
                _downedHarmony.Patch(AccessTools.Method(_reviveType, "StartRevive"),
                    prefix: new HarmonyMethod(typeof(BodyDragFikaBridge), nameof(DownedStartRevivePrefix)));
                _downedHarmony.Patch(AccessTools.Method(typeof(ObservedPlayer), nameof(ObservedPlayer.ToggleDowned)),
                    prefix: new HarmonyMethod(typeof(BodyDragFikaBridge), nameof(DownedToggleDownedPrefix)));
                _downedHarmony.Patch(AccessTools.Method(typeof(Player), nameof(Player.OnDead)),
                    prefix: new HarmonyMethod(typeof(BodyDragFikaBridge), nameof(DownedOnDeadPrefix)));
                _log?.LogInfo("[BodyDragFika] downed-player drag hooks installed");
            }
            catch (Exception ex)
            {
                _log?.LogError($"[BodyDragFika] downed-player drag hooks failed: {ex}");
                _reviveType = null;
            }
        }

        private static void ShutdownDowned()
        {
            ClearDowned();
            _downedHarmony?.UnpatchSelf();
            _downedHarmony = null;
        }

        private static void ClearDowned()
        {
            foreach (DownedClaim claim in DownedClaims.Values)
                claim.Drive?.Dispose();
            DownedClaims.Clear();
            _draggingDowned = null;
            BodyDragSync.ExternalDragActive = false;
        }

        private static void RegisterDownedServer(FikaServer server, int epoch) =>
            server.RegisterPacket<DownedDragPacket, NetPeer>((packet, peer) => Enqueue(() =>
            {
                ApplyDowned(packet);
                server.SendData(ref packet, DeliveryMethod.ReliableOrdered, peer);
            }, epoch));

        private static void RegisterDownedClient(FikaClient client, int epoch) =>
            client.RegisterPacket<DownedDragPacket>(packet => Enqueue(() => ApplyDowned(packet), epoch));

        // ---- claims ---------------------------------------------------------------------------
        private static void ApplyDowned(DownedDragPacket packet)
        {
            if (string.IsNullOrEmpty(packet.Downed) || string.IsNullOrEmpty(packet.Dragger))
                return;
            if (packet.Op == DownedDragPacket.Start)
            {
                if (!DownedClaims.TryGetValue(packet.Downed, out DownedClaim claim))
                    DownedClaims[packet.Downed] = claim = new DownedClaim { Dragger = packet.Dragger };
                claim.Dragger = packet.Dragger;
                claim.LastSeen = Time.unscaledTime;
            }
            else if (packet.Op == DownedDragPacket.Stop &&
                DownedClaims.TryGetValue(packet.Downed, out DownedClaim stopped) && stopped.Dragger == packet.Dragger)
                EndDowned(packet.Downed);
        }

        private static void EndDowned(string downedId)
        {
            if (!DownedClaims.TryGetValue(downedId, out DownedClaim claim))
                return;
            claim.Drive?.Dispose();
            DownedClaims.Remove(downedId);
            if (_draggingDowned == downedId)
            {
                _draggingDowned = null;
                BodyDragSync.ExternalDragActive = false;
                RestoreHands();
            }
        }

        // Any end of the local drag (release, target revived/died/gone, leash) re-equips what was stowed.
        private static void RestoreHands()
        {
            Item item = _previousHandsItem;
            _previousHandsItem = null;
            Player me = GamePlayerOwner.MyPlayer;
            if (item == null || me == null || !me.HealthController.IsAlive || !me.IsItemCanBeEquipped(item))
                return;
            me.TryProceed(item, result =>
            {
                if (!string.IsNullOrEmpty(result.Error))
                    _log?.LogWarning($"[BodyDragFika] could not re-equip after drag: {result.Error}");
            });
        }

        private static void SendDowned(byte op, string downed, string dragger)
        {
            if (!BodyDragSync.Active)
                return;
            DownedDragPacket packet = new DownedDragPacket { Op = op, Downed = downed, Dragger = dragger };
            if (BodyDragSync.IsHost)
                Singleton<FikaServer>.Instance?.SendData(ref packet, DeliveryMethod.ReliableOrdered);
            else
                Singleton<FikaClient>.Instance?.SendData(ref packet, DeliveryMethod.ReliableOrdered);
        }

        private static void StartDownedDrag(GamePlayerOwner owner, Player downed)
        {
            Player me = owner?.Player;
            if (me == null || downed == null || _draggingDowned != null || me.ProfileId == downed.ProfileId)
                return;
            if (DownedClaims.ContainsKey(downed.ProfileId))
                return;
            // remember what was in hand (as the corpse drag does) so releasing puts it back
            _previousHandsItem = null;
            if (!me.HandsIsEmpty)
            {
                me.TrySaveLastItemInHands();
                _previousHandsItem = me.LastEquippedWeaponOrKnifeItem;
                me.SetEmptyHands(null);
            }
            _draggingDowned = downed.ProfileId;
            BodyDragSync.ExternalDragActive = true;
            ApplyDowned(new DownedDragPacket { Op = DownedDragPacket.Start, Downed = downed.ProfileId, Dragger = me.ProfileId });
            SendDowned(DownedDragPacket.Start, downed.ProfileId, me.ProfileId);
            _nextDownedBeat = Time.unscaledTime + DownedHeartbeatSeconds;
        }

        private static void StopDownedDrag(string reason)
        {
            string id = _draggingDowned;
            Player me = GamePlayerOwner.MyPlayer;
            if (id == null || me == null)
                return;
            _log?.LogInfo($"[BodyDragFika] released downed {id} ({reason})");
            SendDowned(DownedDragPacket.Stop, id, me.ProfileId);
            EndDowned(id);
        }

        // ---- per-frame ------------------------------------------------------------------------
        private static Player FindPlayer(string profileId) =>
            string.IsNullOrEmpty(profileId) || !Singleton<GameWorld>.Instantiated
                ? null
                : Singleton<GameWorld>.Instance.GetEverExistedPlayerByID(profileId);

        private static bool IsDowned(Player player) =>
            player is ObservedPlayer observed ? observed.Downed :
            player is FikaPlayer local && local.HealthController is Fika.Core.Main.ClientClasses.ClientHealthController health && health.Downed;

        private static void TickDowned()
        {
            if (DownedClaims.Count == 0 || !Plugin.Enabled.Value)
                return;
            float now = Time.unscaledTime;
            Player me = GamePlayerOwner.MyPlayer;

            if (_draggingDowned != null)
            {
                string reason = null;
                Player target = FindPlayer(_draggingDowned);
                if (me == null || !me.HealthController.IsAlive)
                    reason = "dragger-down";
                else if (target == null || !IsDowned(target))
                    reason = "target-gone";
                else if (!DownedClaims.TryGetValue(_draggingDowned, out DownedClaim own) || own.Dragger != me.ProfileId)
                    reason = "claim-lost";
                else if ((me.Position - target.Position).sqrMagnitude > DownedLeashDistance * DownedLeashDistance)
                    reason = "leash";
                if (reason != null)
                    StopDownedDrag(reason);
                else if (now >= _nextDownedBeat)
                {
                    _nextDownedBeat = now + DownedHeartbeatSeconds;
                    SendDowned(DownedDragPacket.Start, _draggingDowned, me.ProfileId);
                    DownedClaims[_draggingDowned].LastSeen = now;
                }
            }

            DownedScratch.Clear();
            DownedScratch.AddRange(DownedClaims.Keys);
            foreach (string downedId in DownedScratch)
            {
                if (!DownedClaims.TryGetValue(downedId, out DownedClaim claim))
                    continue;
                Player downed = FindPlayer(downedId);
                Player dragger = FindPlayer(claim.Dragger);
                if (now - claim.LastSeen > DownedExpirySeconds || downed == null || !IsDowned(downed) ||
                    dragger == null || !dragger.HealthController.IsAlive)
                {
                    EndDowned(downedId);
                    continue;
                }
                if (downed.IsYourPlayer)
                    MoveOwnPlayer(downed, dragger);
                else if (downed is ObservedPlayer)
                    DriveObservedRagdoll(claim, downed, dragger);
            }
        }

        // The downed player's own client: walk the real position toward the trail point.
        // Fika freezes the character controller (IsMoveIgnored) while downed, which makes Move a
        // no-op - lift it for exactly this one call. IsAxesIgnored stays set, so no input moves them.
        private static void MoveOwnPlayer(Player self, Player dragger)
        {
            float deltaTime = Time.deltaTime;
            if (deltaTime <= 0f)
                return;
            Vector3 root = self.Position;
            Vector3 motion = DownedDragMath.FollowMotion(DownedDragMath.TrailPoint(dragger.Position, root), root, deltaTime);
            if (motion.sqrMagnitude < 1e-8f)
                return;
            motion.y = -DownedDragMath.GroundStickSpeed * deltaTime;

            ICharacterController controller = self.MovementContext.CharacterController;
            SimpleCharacterController simple = controller as SimpleCharacterController;
            bool wasIgnored = simple != null && simple.IsMoveIgnored;
            float savedLimit = controller.SpeedLimit;
            try
            {
                if (simple != null)
                    simple.IsMoveIgnored = false;
                controller.SpeedLimit = -1f;
                self.MovementContext.DirectApplyMotion(motion, deltaTime);
            }
            finally
            {
                controller.SpeedLimit = savedLimit;
                if (simple != null)
                    simple.IsMoveIgnored = wasIgnored;
            }
        }

        private static void DriveObservedRagdoll(DownedClaim claim, Player downed, Player dragger)
        {
            if (claim.Drive == null && !claim.NoDrive)
            {
                Component interactable = _reviveType != null ? downed.GetComponent(_reviveType) : null;
                CorpseRagdoll ragdoll = interactable != null ? _reviveRagdollField.GetValue(interactable) as CorpseRagdoll : null;
                string failure = "no ragdoll (headless, or not built yet)";
                claim.Drive = ragdoll != null ? DownedRagdollDrive.TryCreate(ragdoll, downed.ProfileId, _log, out failure) : null;
                if (claim.Drive == null)
                {
                    claim.NoDrive = true;
                    _log?.LogInfo($"[BodyDragFika] downed ragdoll drive unavailable for {downed.ProfileId}: {failure}");
                }
                else
                    _log?.LogInfo($"[BodyDragFika] downed ragdoll drive started for {downed.ProfileId} " +
                        $"(Position={downed.Position}, gameObject={downed.gameObject.transform.position})");
            }
            if (claim.Drive == null)
                return;
            if (!claim.Drive.Tick(DownedDragMath.RemoteHold(downed.Position, dragger.Position), Time.deltaTime))
            {
                _log?.LogWarning($"[BodyDragFika] downed ragdoll drive aborted for {downed.ProfileId} (invalid simulation state)");
                claim.Drive.Dispose();
                claim.Drive = null;
                claim.NoDrive = true;
            }
        }

        // ---- Harmony hooks --------------------------------------------------------------------
        // The general GetAvailableActions throws for an InteractableObject type it doesn't know, before
        // any postfix could run - so, like Fika does for ReviveInteractable, answer for the stand-in
        // in a prefix. The ragdoll is detached while dragged; Revive is blocked then, so the menu is
        // just RELEASE for the dragger (empty for everyone else).
        private static bool DownedDragMenuPrefix(GamePlayerOwner owner, IInteractive interactive, ref AvailableInteractionState __result)
        {
            if (!(interactive is DownedDragInteractable dragged))
                return true;
            AvailableInteractionState state = new AvailableInteractionState();
            Player holder = owner?.Player;
            if (holder != null && DownedClaims.TryGetValue(dragged.DownedId, out DownedClaim own) && own.Dragger == holder.ProfileId)
                state.Actions.Add(new InteractionAction
                {
                    Name = "RELEASE",
                    TargetName = FindPlayer(dragged.DownedId)?.Profile.Nickname ?? "",
                    Action = () => StopDownedDrag("menu")
                });
            __result = state;
            return false;
        }

        // Adds DRAG BODY / RELEASE to Fika's revive menu and greys out REVIVE while a drag is active.
        // Fika's own prefix returns false for ReviveInteractable, but postfixes still run.
        private static void DownedMenuPostfix(GamePlayerOwner owner, IInteractive interactive, ref AvailableInteractionState __result)
        {
            if (_reviveType == null || interactive == null || __result == null || interactive.GetType() != _reviveType ||
                !Plugin.Enabled.Value)
                return;
            ObservedPlayer target = ((Component)interactive).GetComponent<ObservedPlayer>();
            Player me = owner?.Player;
            if (target == null || me == null)
                return;

            bool claimed = DownedClaims.TryGetValue(target.ProfileId, out DownedClaim claim);
            bool mine = claimed && claim.Dragger == me.ProfileId;
            if (claimed)
                foreach (InteractionAction action in __result.Actions)
                    if (action.Action != null && action.Action.Method.Name == "StartRevive")
                        action.Disabled = true;

            string targetName = target.Profile.Nickname;
            if (mine)
                __result.Actions.Add(new InteractionAction
                {
                    Name = "RELEASE", TargetName = targetName, Action = () => StopDownedDrag("menu")
                });
            else if (!claimed && _draggingDowned == null && me.HealthController.IsAlive)
                __result.Actions.Add(new InteractionAction
                {
                    Name = "DRAG BODY", TargetName = targetName, Action = () => StartDownedDrag(owner, target)
                });
        }

        // Race guard: a menu list built before the claim can still be clicked afterwards.
        private static bool DownedStartRevivePrefix(object __instance)
        {
            ObservedPlayer target = ((Component)__instance).GetComponent<ObservedPlayer>();
            return target == null || !DownedClaims.ContainsKey(target.ProfileId);
        }

        // Revived: the bones must be back under the root before Fika's RemoveRagdoll re-enables animators.
        private static void DownedToggleDownedPrefix(ObservedPlayer __instance, bool downed)
        {
            if (!downed)
                EndDowned(__instance.ProfileId);
        }

        // Died: bones back under the root before the corpse is built from the player.
        private static void DownedOnDeadPrefix(Player __instance)
        {
            if (__instance != null && DownedClaims.Count > 0)
                EndDowned(__instance.ProfileId);
        }
    }
}
