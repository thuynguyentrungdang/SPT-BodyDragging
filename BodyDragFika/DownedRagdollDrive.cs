using System.Collections.Generic;
using BepInEx.Logging;
using BodyDragging.Features;
using EFT;
using EFT.Interactive;
using UnityEngine;

namespace BodyDragFika
{
    // EFT finds the interactive for a raycast hit with GetComponentInParent<InteractableObject>().
    // Detaching the ragdoll from the player root cuts the bones off from Fika's ReviveInteractable,
    // so this stand-in rides on the detached top bone and gives the dragger their RELEASE menu.
    public sealed class DownedDragInteractable : InteractableObject
    {
        public string DownedId;
    }

    // Pulls Fika's locally simulated downed-player ragdoll with a kinematic hand tethered to the
    // chest (KeepMeAlive's BodyRagdoll technique, MIT). Every peer runs its own copy; the hold point
    // comes from the synced player root and the dragger, so nothing but the claim is networked.
    internal sealed class DownedRagdollDrive
    {
        private const float HoldHeight = 0.45f;
        private const float HoldSlack = 0.03f;
        private const float MaxHandSpeed = 6f;
        private const float TeleportDistance = 2.5f;
        private const float MaxHoldError = 2f;

        private struct Detached
        {
            internal Transform Transform;
            internal Transform Parent;
            internal int Sibling;
        }

        private readonly List<Rigidbody> _bodies = new();
        private readonly List<Detached> _tops = new();
        private readonly List<DownedDragInteractable> _interactables = new();
        private readonly RagdollJointStability _joints = new();
        private Rigidbody _chest;
        private Rigidbody _handBody;
        private GameObject _hand;
        private Vector3 _lastTarget;

        internal int BodyCount => _bodies.Count;

        internal static DownedRagdollDrive TryCreate(CorpseRagdoll ragdoll, string downedId, ManualLogSource log, out string failure)
        {
            failure = null;
            RigidbodySpawner[] spawners = ragdoll?._rigidbodySpawners;
            if (spawners == null || spawners.Length == 0)
            {
                failure = "no ragdoll spawners";
                return null;
            }

            DownedRagdollDrive drive = new DownedRagdollDrive();
            int kinematic = 0;
            foreach (RigidbodySpawner spawner in spawners)
            {
                Rigidbody body = spawner != null ? spawner.Rigidbody : null;
                if (body == null)
                    continue;
                drive._bodies.Add(body);
                if (body.isKinematic)
                    kinematic++;
                if (drive._chest == null && spawner.TryGetComponent(out BodyPartCollider part) &&
                    part.BodyPartColliderType == EBodyPartColliderType.RibcageUp)
                    drive._chest = body;
            }

            log?.LogInfo($"[BodyDragFika] downed ragdoll: {drive._bodies.Count}/{spawners.Length} bodies, {kinematic} kinematic, chest={(drive._chest != null)}");
            if (drive._chest == null || kinematic > 0)
            {
                failure = drive._chest == null ? "no chest body" : "bodies are kinematic (another mod controls this ragdoll)";
                return null;
            }

            drive._joints.Capture(ragdoll._jointSpawners);
            drive.DetachFromRoot();
            foreach (Detached top in drive._tops)
            {
                DownedDragInteractable interactable = top.Transform.gameObject.AddComponent<DownedDragInteractable>();
                interactable.DownedId = downedId;
                drive._interactables.Add(interactable);
            }
            drive.CreateHand();
            return drive;
        }

        // The top of the ragdoll (rigidbodies with no ragdoll rigidbody above them - normally the pelvis)
        // is taken out from under the player so Fika moving the player root doesn't carry the bones.
        private void DetachFromRoot()
        {
            HashSet<Transform> transforms = new();
            foreach (Rigidbody body in _bodies)
                transforms.Add(body.transform);

            foreach (Transform transform in transforms)
            {
                bool top = true;
                for (Transform parent = transform.parent; parent != null; parent = parent.parent)
                {
                    if (transforms.Contains(parent))
                    {
                        top = false;
                        break;
                    }
                }
                if (!top)
                    continue;
                _tops.Add(new Detached { Transform = transform, Parent = transform.parent, Sibling = transform.GetSiblingIndex() });
                transform.SetParent(null, true);
            }
        }

        private void ReattachToRoot()
        {
            foreach (Detached top in _tops)
            {
                if (top.Transform == null || top.Parent == null)
                    continue;
                top.Transform.SetParent(top.Parent, true);
                top.Transform.SetSiblingIndex(top.Sibling);
            }
            _tops.Clear();
        }

        private void CreateHand()
        {
            _hand = new GameObject("BodyDrag Downed Hand");
            _hand.transform.position = _chest.worldCenterOfMass;

            _handBody = _hand.AddComponent<Rigidbody>();
            _handBody.isKinematic = true;
            _handBody.useGravity = false;

            ConfigurableJoint joint = _hand.AddComponent<ConfigurableJoint>();
            joint.autoConfigureConnectedAnchor = false;
            joint.anchor = Vector3.zero;
            joint.connectedAnchor = _chest.centerOfMass;
            joint.connectedBody = _chest;
            joint.xMotion = ConfigurableJointMotion.Limited;
            joint.yMotion = ConfigurableJointMotion.Limited;
            joint.zMotion = ConfigurableJointMotion.Limited;
            joint.linearLimit = new SoftJointLimit { limit = HoldSlack };
            joint.angularXMotion = ConfigurableJointMotion.Free;
            joint.angularYMotion = ConfigurableJointMotion.Free;
            joint.angularZMotion = ConfigurableJointMotion.Free;
            joint.projectionMode = JointProjectionMode.PositionAndRotation;
            joint.projectionDistance = 0.1f;
            joint.enablePreprocessing = false;

            _lastTarget = _chest.worldCenterOfMass;
        }

        // false = the simulation went bad; the caller drops the drive.
        internal bool Tick(Vector3 hold, float deltaTime)
        {
            if (_chest == null || _handBody == null || deltaTime <= 0f)
                return false;
            Vector3 chest = _chest.position;
            if (!IsFinite(chest) || !IsFinite(hold))
                return false;

            _joints.RepairExcessiveSeparation(deltaTime);
            if (_chest.IsSleeping())
                _chest.WakeUp();

            Vector3 target = hold + Vector3.up * HoldHeight;
            Vector3 jump = target - _lastTarget;
            if (jump.sqrMagnitude > TeleportDistance * TeleportDistance)
                Shift(jump);
            else if ((chest - target).sqrMagnitude > MaxHoldError * MaxHoldError)
                Shift(target - chest);
            _lastTarget = target;

            _handBody.MovePosition(Vector3.MoveTowards(_handBody.position, target, MaxHandSpeed * deltaTime));
            return true;
        }

        private void Shift(Vector3 offset)
        {
            foreach (Rigidbody body in _bodies)
            {
                if (body == null)
                    continue;
                body.position += offset;
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
            if (_handBody != null)
                _handBody.position += offset;
        }

        internal void Dispose()
        {
            if (_hand != null)
            {
                // Deactivated first: Destroy lands at end of frame and the joint would keep pulling
                // the chest through any physics step before then.
                _hand.SetActive(false);
                Object.Destroy(_hand);
            }
            _hand = null;
            _handBody = null;
            _joints.Restore();
            foreach (DownedDragInteractable interactable in _interactables)
                if (interactable != null)
                    Object.Destroy(interactable);
            _interactables.Clear();
            ReattachToRoot();
            // leave the bodies exactly where they are, just not flung on release
            foreach (Rigidbody body in _bodies)
            {
                if (body == null)
                    continue;
                body.velocity = Vector3.ClampMagnitude(body.velocity, 2f);
                body.angularVelocity = Vector3.ClampMagnitude(body.angularVelocity, 4f);
            }
        }

        private static bool IsFinite(Vector3 v) =>
            !float.IsNaN(v.x) && !float.IsInfinity(v.x) && !float.IsNaN(v.y) && !float.IsInfinity(v.y) &&
            !float.IsNaN(v.z) && !float.IsInfinity(v.z);
    }
}
