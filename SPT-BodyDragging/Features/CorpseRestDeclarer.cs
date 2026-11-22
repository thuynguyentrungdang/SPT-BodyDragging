using System.Collections.Generic;
using EFT;
using EFT.Interactive;
using JsonType;
using UnityEngine;

namespace BodyDragging.Features
{
    // The peer that let go of a body says where it came to rest (adapted from KMA Body Drag's
    // rest declaration, MIT - see NOTICE). Every peer's copy settles under its own physics and
    // would end up a little differently; once this peer's copy has stopped moving it sends the
    // corpse root and every bone once (BodyDragSync.RestDeclared), and all copies, this one
    // included, are set down on it.
    internal static class CorpseRestDeclarer
    {
        // a body let go counts as settled once every part is slower than this (m/s) and spins less
        // than SettleSpinPerSpeed times that; the timeout settles it regardless, MinSettle lets a
        // body dropped in the air start falling first
        private const float SettleSpeed = 1f;
        private const float SettleSpinPerSpeed = 5f;
        private const float MinSettleSeconds = 0.3f;
        private const float SettleTimeoutSeconds = 3f;
        // how far under the pelvis the corpse root may be found
        private const float GroundProbe = 1.5f;

        private static readonly Dictionary<Corpse, float> Pending = new();
        private static readonly List<Corpse> Scratch = new();

        internal static void Begin(Corpse corpse)
        {
            if (corpse != null && BodyDragSync.Active)
                Pending[corpse] = Time.unscaledTime;
        }

        internal static void Cancel(Corpse corpse)
        {
            if (corpse != null && Pending.Count != 0)
                Pending.Remove(corpse);
        }

        internal static void Tick()
        {
            if (Pending.Count == 0)
                return;
            float now = Time.unscaledTime;
            Scratch.Clear();
            foreach (KeyValuePair<Corpse, float> entry in Pending)
            {
                float elapsed = now - entry.Value;
                if (entry.Key == null || !BodyDragSync.Active || elapsed >= SettleTimeoutSeconds ||
                    (elapsed >= MinSettleSeconds && IsSettled(entry.Key)))
                    Scratch.Add(entry.Key);
            }
            foreach (Corpse corpse in Scratch)
            {
                Pending.Remove(corpse);
                if (corpse != null && BodyDragSync.Active)
                    Declare(corpse, true, false);
            }
            Scratch.Clear();
        }

        internal static void Clear()
        {
            Pending.Clear();
            Scratch.Clear();
        }

        internal static bool IsSettled(Corpse corpse)
        {
            RigidbodySpawner[] spawners = corpse.Ragdoll?._rigidbodySpawners;
            if (spawners == null)
                return true;
            float speedSq = SettleSpeed * SettleSpeed;
            float spin = SettleSpeed * SettleSpinPerSpeed;
            foreach (RigidbodySpawner spawner in spawners)
            {
                Rigidbody body = spawner != null ? spawner.Rigidbody : null;
                if (body == null || body.isKinematic)
                    continue;
                if (body.velocity.sqrMagnitude > speedSq || body.angularVelocity.sqrMagnitude > spin * spin)
                    return false;
            }
            return true;
        }

        // send: tell the other peers (BodyDragSync.RestDeclared). handoff: this copy is held by a Rupture handoff lease
        // (HandoffLeases) that adopts it right after, so it is set down without EFT's own stop cycle.
        internal static void Declare(Corpse corpse, bool send, bool handoff)
        {
            RigidbodySpawner[] spawners = corpse.Ragdoll?._rigidbodySpawners;
            if (spawners == null || spawners.Length == 0)
                return;
            TransformSync[] bones = corpse.GetTransformSync();
            Vector3[] positions = new Vector3[bones.Length];
            Quaternion[] rotations = new Quaternion[bones.Length];
            for (int i = 0; i < bones.Length; i++)
            {
                positions[i] = bones[i].Position;
                rotations[i] = bones[i].Rotation;
            }
            Vector3 root = GroundUnderPelvis(corpse, spawners);
            if (send)
            BodyDragSync.RestDeclared?.Invoke(new BodyDragRest
            {
                ProfileId = corpse.PlayerProfileID,
                Root = root,
                BonePositions = positions,
                BoneRotations = rotations
            });
            // this copy goes where it says too: the corpse root moves under the body, so anything that
            // reads the corpse's own position (loot, culling, bot looting) finds it where it lies
            CorpseRagdollSettlement.SetDown(corpse, root, bones, handoff);
            BodyDragLog.Info($"[CorpseDrag] Declared where {corpse.PlayerProfileID} came to rest");
        }

        // the ground under the pelvis, or the pelvis itself over a gap
        private static Vector3 GroundUnderPelvis(Corpse corpse, RigidbodySpawner[] spawners)
        {
            Vector3 pelvis = corpse.transform.position;
            foreach (RigidbodySpawner spawner in spawners)
                if (spawner != null && spawner.TryGetComponent(out BodyPartCollider part) &&
                    part.BodyPartColliderType == EBodyPartColliderType.Pelvis)
                {
                    pelvis = spawner.transform.position;
                    break;
                }
            Vector3 from = pelvis + Vector3.up * 0.5f;
            return Physics.Raycast(from, Vector3.down, out RaycastHit hit, GroundProbe + 0.5f,
                LayersMaskController.HighPolyWithTerrainMask, QueryTriggerInteraction.Ignore)
                ? hit.point
                : pelvis;
        }
    }
}
