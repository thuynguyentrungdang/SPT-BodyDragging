using System;
using System.Collections.Generic;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using EFT.ItemInHandSubsystem;
using HarmonyLib;
using UnityEngine;

namespace BodyDragging.Features
{
    internal enum ArmedDragMode
    {
        Off,
        PistolOnly,
        AnyWeapon
    }

    // What the dragger's hands do during any drag (corpse, Rupture-managed, Fika downed): keep or
    // draw a one-handed weapon and raise the left arm into EFT's own left-hand-item pose (the
    // compass pose - weapon stays in the right hand), with the left wrist IK'd onto the dragged
    // body's chest, or fall back to empty hands. ADS is blocked while armed. The pose state is
    // mirrored to other peers through BodyDragSync.HandPoseSent / ApplyRemote.
    internal static class DragHands
    {
        private const float EmptyHandsRetryDelay = 0.25f;
        private const float EmptyHandsRequestTimeout = 1.5f;
        private const float PoseHeartbeatSeconds = 2f;
        private const float RemoteExpirySeconds = 7f;
        private const float ChestRetrySeconds = 0.75f;
        // where the palm lands: toward the dragger from the chest centre, a little up (collar)
        private const float GripTowardDragger = 0.1f;
        private const float GripUp = 0.1f;

        private sealed class HandState
        {
            internal Player Player;
            internal string BodyId;
            internal Rigidbody Chest;
            internal GripPose Grip;
            internal bool PoseApplied;
            internal bool PoseOn;
            internal float NextChestTry;
            internal float LastSeen;
        }

        private static object _owner;
        private static Player _player;
        private static Item _previous;
        private static bool _restore;
        private static bool _enforceEmpty;
        private static bool _emptyPending;
        private static float _nextEmptyRequest;
        private static float _emptyExpires;
        private static bool _armed;
        private static float _nextBeat;
        private static HandState _local;

        private static readonly Dictionary<string, HandState> Remote = new();
        private static readonly List<string> RemoteScratch = new();

        internal static bool BlocksAim(Player player) => _armed && _player != null && ReferenceEquals(player, _player);

        internal static void Begin(Player player, object owner, string bodyId)
        {
            if (player == null || _player != null)
                return;
            _owner = owner;
            _player = player;
            _previous = null;
            _restore = false;
            _enforceEmpty = false;
            _emptyPending = false;
            _armed = false;
            _nextBeat = 0f;
            _local = null;

            Weapon weapon = Plugin.ArmedDrag.Value == ArmedDragMode.Off ? null : PickWeapon(player, Plugin.ArmedDrag.Value);
            if (weapon != null)
            {
                _armed = true;
                _local = new HandState { Player = player, BodyId = bodyId };
                SaveHands(player);
                if (!ReferenceEquals(player.TryGetItemInHands<Item>(), weapon))
                    player.TryProceed(weapon, result =>
                    {
                        if (!string.IsNullOrEmpty(result.Error))
                            BodyDragLog.Warning($"[DragHands] could not draw weapon: {result.Error}");
                    });
                return;
            }

            if (!Plugin.UnequipHandsWhileDragging.Value)
                return;
            if (!player.HandsIsEmpty)
                SaveHands(player);
            _enforceEmpty = true;
            RequestEmptyHands();
        }

        private static void SaveHands(Player player)
        {
            if (player.HandsIsEmpty)
                return;
            player.TrySaveLastItemInHands();
            _previous = player.LastEquippedWeaponOrKnifeItem;
            _restore = _previous != null;
        }

        private static Weapon PickWeapon(Player player, ArmedDragMode mode)
        {
            Weapon held = player.TryGetItemInHands<Weapon>();
            if (held != null && Eligible(held, mode))
                return held;
            Weapon holster = player.Equipment?.GetSlot(EquipmentSlot.Holster)?.ContainedItem as Weapon;
            return holster != null && Eligible(holster, mode) && player.IsItemCanBeEquipped(holster) ? holster : null;
        }

        private static bool Eligible(Weapon weapon, ArmedDragMode mode) =>
            mode == ArmedDragMode.AnyWeapon || weapon.WeapClass == "pistol";

        internal static void Tick()
        {
            Player player = _player;
            if (player == null)
                return;
            if (_enforceEmpty)
                RequestEmptyHands();
            if (!_armed || _local == null)
                return;

            if (player.HandsController is Player.FirearmController firearm && firearm.IsAiming)
                firearm.SetAim(false);

            bool want = PoseWanted(player);
            ApplyPose(player, want, ref _local.PoseApplied);
            if (!want)
                ReleaseIk(_local);

            float now = Time.unscaledTime;
            if (want != _local.PoseOn || (want && now >= _nextBeat))
            {
                _local.PoseOn = want;
                _nextBeat = now + PoseHeartbeatSeconds;
                BodyDragSync.HandPoseSent?.Invoke(want, _local.BodyId);
            }
        }

        // arm up only while the weapon is steady in hand; reloads need the left hand
        private static bool PoseWanted(Player player)
        {
            if (!(player.HandsController is Player.FirearmController firearm) || player.InventoryController.IsChangingWeapon)
                return false;
            return !firearm.IsAiming && !firearm.IsInReloadOperation() && !firearm.IsInRemoveOperation();
        }

        private static void ApplyPose(Player player, bool on, ref bool applied)
        {
            ILeftHandController hand = player != null ? player.LeftHandController : null;
            if (hand == null)
                return;
            try
            {
                if (on)
                {
                    if (!hand.IsUsing)
                    {
                        hand.ToggleHand(null);
                        applied = true;
                    }
                }
                else if (applied)
                {
                    applied = false;
                    hand.RemoveItem(1f);
                }
            }
            catch (Exception exception)
            {
                applied = false;
                BodyDragLog.Warning($"[DragHands] left-hand pose failed: {exception.Message}");
            }
        }

        private static void RequestEmptyHands()
        {
            Player player = _player;
            if (!_enforceEmpty || player == null || player.HandsIsEmpty || Time.unscaledTime < _nextEmptyRequest)
                return;
            if (_emptyPending && Time.unscaledTime < _emptyExpires)
                return;

            _emptyPending = true;
            _nextEmptyRequest = Time.unscaledTime + EmptyHandsRetryDelay;
            _emptyExpires = Time.unscaledTime + EmptyHandsRequestTimeout;
            player.SetEmptyHands(result =>
            {
                _emptyPending = false;
                if (!string.IsNullOrEmpty(result.Error))
                    BodyDragLog.Warning($"[DragHands] empty-hands transition failed: {result.Error}");
            });
        }

        internal static void End(object owner)
        {
            if (_player == null || !ReferenceEquals(owner, _owner))
                return;
            Player player = _player;
            HandState local = _local;
            bool restore = _restore;
            Item previous = _previous;
            Reset();

            if (player == null)
                return;
            if (local != null)
            {
                ReleaseIk(local);
                DestroyGrip(local);
                if (local.PoseOn || local.PoseApplied)
                {
                    bool applied = true;
                    ApplyPose(player, false, ref applied);
                    BodyDragSync.HandPoseSent?.Invoke(false, local.BodyId);
                }
            }
            if (!restore || previous == null || player.HealthController == null || !player.HealthController.IsAlive ||
                ReferenceEquals(player.TryGetItemInHands<Item>(), previous) || !player.IsItemCanBeEquipped(previous))
                return;
            player.TryProceed(previous, result =>
            {
                if (!string.IsNullOrEmpty(result.Error))
                    BodyDragLog.Warning($"[DragHands] previous-item equip failed: {result.Error}");
            });
        }

        // raid over / owner gone: drop state without touching players that no longer exist
        internal static void Abandon(object owner)
        {
            if (_player != null && ReferenceEquals(owner, _owner))
                Reset();
        }

        private static void Reset()
        {
            _player = null;
            _owner = null;
            _previous = null;
            _restore = false;
            _enforceEmpty = false;
            _armed = false;
            _local = null;
        }

        // ---- other peers' draggers ------------------------------------------------------------
        internal static void ApplyRemote(string profileId, bool on, string bodyId)
        {
            if (string.IsNullOrEmpty(profileId) || !Singleton<GameWorld>.Instantiated)
                return;
            Player player = Singleton<GameWorld>.Instance.GetEverExistedPlayerByID(profileId);
            if (player == null || player.IsYourPlayer)
                return;
            if (on)
            {
                if (!Remote.TryGetValue(profileId, out HandState state))
                    Remote[profileId] = state = new HandState { Player = player };
                state.Player = player;
                state.BodyId = bodyId;
                state.PoseOn = true;
                state.LastSeen = Time.unscaledTime;
                ApplyPose(player, true, ref state.PoseApplied);
            }
            else if (Remote.TryGetValue(profileId, out HandState state))
            {
                Remote.Remove(profileId);
                state.PoseOn = false;
                ReleaseIk(state);
                DestroyGrip(state);
                bool applied = true;
                ApplyPose(player, false, ref applied);
            }
        }

        internal static void TickRemote()
        {
            if (Remote.Count == 0)
                return;
            float now = Time.unscaledTime;
            RemoteScratch.Clear();
            foreach (KeyValuePair<string, HandState> entry in Remote)
                if (now - entry.Value.LastSeen > RemoteExpirySeconds || entry.Value.Player == null)
                    RemoteScratch.Add(entry.Key);
            foreach (string profileId in RemoteScratch)
                ApplyRemote(profileId, false, null);
        }

        internal static void ClearRemote()
        {
            foreach (HandState state in Remote.Values)
                DestroyGrip(state);
            Remote.Clear();
        }

        // ---- left wrist IK onto the dragged chest ---------------------------------------------
        // Called from the Player.IkProcess prefix for every player every frame: the first two
        // checks are what keep it free for everybody who isn't dragging.
        internal static bool PrepareIk(Player player, out float weight)
        {
            weight = 0f;
            if (_local == null && Remote.Count == 0)
                return false;
            HandState state = null;
            if (_local != null && ReferenceEquals(player, _local.Player))
                state = _local;
            else if (Remote.Count > 0 && player.ProfileId != null)
                Remote.TryGetValue(player.ProfileId, out state);
            if (state == null || !state.PoseOn)
                return false;

            if ((state.Chest == null || state.Chest.gameObject == null) && Time.unscaledTime >= state.NextChestTry)
            {
                state.NextChestTry = Time.unscaledTime + ChestRetrySeconds;
                state.Chest = ResolveChest(state.BodyId);
            }
            if (state.Chest == null)
                return false;

            if (state.Grip == null)
            {
                GameObject grip = new GameObject("BodyDragGrip");
                state.Grip = grip.AddComponent<GripPose>();
            }
            Vector3 centre = state.Chest.worldCenterOfMass;
            Vector3 toDragger = player.Position - centre;
            toDragger.y = 0f;
            toDragger = toDragger.sqrMagnitude > 1e-4f ? toDragger.normalized : Vector3.zero;
            Transform palm = player.PlayerBones != null ? player.PlayerBones.LeftPalm : null;
            state.Grip.transform.SetPositionAndRotation(
                centre + toDragger * GripTowardDragger + Vector3.up * GripUp,
                palm != null ? palm.rotation : Quaternion.identity);

            player.LeftHandInteractionTarget = state.Grip;
            player.ThirdIkWeight.Target = 1f;
            weight = player.ThirdIkWeight.Value;
            return weight > 0f;
        }

        private static void ReleaseIk(HandState state)
        {
            Player player = state?.Player;
            if (player == null || state.Grip == null)
                return;
            if (ReferenceEquals(player.LeftHandInteractionTarget, state.Grip))
            {
                player.LeftHandInteractionTarget = null;
                player.ThirdIkWeight.Target = 0f;
            }
        }

        private static void DestroyGrip(HandState state)
        {
            if (state?.Grip != null)
                UnityEngine.Object.Destroy(state.Grip.gameObject);
            if (state != null)
                state.Grip = null;
        }

        private static Rigidbody ResolveChest(string bodyId)
        {
            if (string.IsNullOrEmpty(bodyId))
                return null;
            Rigidbody downed = BodyDragSync.ResolveDownedChest?.Invoke(bodyId);
            if (downed != null)
                return downed;
            RigidbodySpawner[] spawners = RemoteCorpseDragFollower.FindCorpse(bodyId)?.Ragdoll?._rigidbodySpawners;
            if (spawners == null)
                return null;
            foreach (RigidbodySpawner spawner in spawners)
                if (spawner != null && spawner.Rigidbody != null && spawner.TryGetComponent(out BodyPartCollider part) &&
                    part.BodyPartColliderType == EBodyPartColliderType.RibcageUp)
                    return spawner.Rigidbody;
            return null;
        }

        // Player._limbs[0] is the left-arm LimbIK. EFT's own weight (1 - the animation's
        // LEFT_HAND_WEIGHT curve) can be 0 while the compass pose plays, which skips the IK entirely;
        // lift it to the blend weight so the IkProcess that follows moves the wrist to the grip.
        private static FieldInfo _limbsField;
        private static MemberInfo _solverMember;
        private static MemberInfo _positionWeight;
        private static MemberInfo _rotationWeight;
        private static bool _ikReflectionFailed;

        internal static void LiftLeftArmWeight(Player player, float weight)
        {
            if (_ikReflectionFailed)
                return;
            try
            {
                _limbsField ??= AccessTools.Field(typeof(Player), "_limbs");
                Array limbs = _limbsField?.GetValue(player) as Array;
                object limb = limbs != null && limbs.Length > 0 ? limbs.GetValue(0) : null;
                if (limb == null)
                    return;
                _solverMember ??= FindMember(limb.GetType(), "solver");
                if (_solverMember == null)
                {
                    _ikReflectionFailed = true;
                    return;
                }
                object solver = GetObject(_solverMember, limb);
                if (solver == null)
                    return;
                _positionWeight ??= FindMember(solver.GetType(), "IKPositionWeight");
                _rotationWeight ??= FindMember(solver.GetType(), "IKRotationWeight");
                if (_positionWeight == null || _rotationWeight == null)
                {
                    _ikReflectionFailed = true;
                    return;
                }
                if (GetFloat(_positionWeight, solver) < weight) SetFloat(_positionWeight, solver, weight);
                if (GetFloat(_rotationWeight, solver) < weight) SetFloat(_rotationWeight, solver, weight);
            }
            catch (Exception exception)
            {
                _ikReflectionFailed = true;
                BodyDragLog.Warning($"[DragHands] left-arm IK weight lift unavailable: {exception.Message}");
            }
        }

        private static MemberInfo FindMember(Type type, string name)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            return (MemberInfo)type.GetField(name, flags) ?? type.GetProperty(name, flags);
        }

        private static object GetObject(MemberInfo member, object target) =>
            member is FieldInfo field ? field.GetValue(target) : ((PropertyInfo)member).GetValue(target);

        private static float GetFloat(MemberInfo member, object target) => (float)GetObject(member, target);

        private static void SetFloat(MemberInfo member, object target, float value)
        {
            if (member is FieldInfo field)
                field.SetValue(target, value);
            else
                ((PropertyInfo)member).SetValue(target, value);
        }
    }
}
