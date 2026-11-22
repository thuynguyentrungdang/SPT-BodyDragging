using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace BodyDragging.Features
{
    // Physics hygiene for the native (non-Rupture) drag, from KMA Body Drag's CorpseBodyRig (MIT,
    // awnova - see NOTICE): EFT's prefab ragdoll is a poor drag target. Its arms are the heaviest
    // parts of the body and nothing caps how fast a limb may fly, so a turning dragger or a joint
    // that starts out of shape whips the whole body around. Applied to the corpse's own
    // rigidbodies for the length of the drag and undone in Restore.
    internal sealed class NativeRigTuning
    {
        // sliding limbs decelerate at ~mu*g (m/s^2); friction combine Maximum so a slick floor
        // mesh doesn't halve the body's traction
        private const float GroundFriction = 9.5f;
        private const float LinearDrag = 0.35f;
        private const float AngularDrag = 1.6f;
        // solver-level caps, applied inside the physics step: each part relative to the pelvis, so
        // dragging the whole body isn't slowed but a limb can't outrun it
        private const float MaxPartSpeed = 3f;
        private const float MaxPartSpin = 10f;
        private const float MaxPelvisSpeed = 12f;

        private struct Original
        {
            internal Rigidbody Body;
            internal float Mass;
            internal float Drag;
            internal float AngularDrag;
            internal float SleepThreshold;
            internal float MaxAngularVelocity;
            internal float MaxLinearVelocity;
            internal Collider Collider;
            internal PhysicMaterial Material;
        }

        private static PhysicMaterial _contactMaterial;
        private readonly List<Original> _originals = new();
        private Rigidbody _pelvis;

        internal void Capture(RigidbodySpawner[] spawners)
        {
            Restore();
            if (spawners == null)
                return;
            PhysicMaterial material = ContactMaterial();
            foreach (RigidbodySpawner spawner in spawners)
            {
                Rigidbody body = spawner != null ? spawner.Rigidbody : null;
                if (body == null)
                    continue;
                EBodyPartColliderType part = spawner.TryGetComponent(out BodyPartCollider collider)
                    ? collider.BodyPartColliderType
                    : EBodyPartColliderType.None;
                Collider contact = body.GetComponent<Collider>();
                _originals.Add(new Original
                {
                    Body = body,
                    Mass = body.mass,
                    Drag = body.drag,
                    AngularDrag = body.angularDrag,
                    SleepThreshold = body.sleepThreshold,
                    MaxAngularVelocity = body.maxAngularVelocity,
                    MaxLinearVelocity = body.maxLinearVelocity,
                    Collider = contact,
                    Material = contact != null ? contact.sharedMaterial : null
                });
                body.mass = HumanSegmentMass(part);
                body.drag = LinearDrag;
                body.angularDrag = AngularDrag;
                // a body that falls asleep stops following the hold
                body.sleepThreshold = 0f;
                body.maxAngularVelocity = MaxPartSpin;
                if (contact != null)
                    contact.sharedMaterial = material;
                if (part == EBodyPartColliderType.Pelvis)
                    _pelvis = body;
            }
        }

        // Once per tick before the step. The grabbed body is the one the hand pulls, so it gets the
        // pelvis cap rather than the part cap.
        internal void LimitSpeeds(Rigidbody grabbed)
        {
            float pelvisSpeed = 0f;
            if (_pelvis != null)
            {
                pelvisSpeed = _pelvis.velocity.magnitude;
                _pelvis.maxLinearVelocity = MaxPelvisSpeed;
            }
            for (int i = 0; i < _originals.Count; i++)
            {
                Rigidbody body = _originals[i].Body;
                if (body == null || body == _pelvis)
                    continue;
                body.maxLinearVelocity = body == grabbed ? MaxPelvisSpeed : pelvisSpeed + MaxPartSpeed;
            }
        }

        internal void Restore()
        {
            for (int i = 0; i < _originals.Count; i++)
            {
                Original original = _originals[i];
                if (original.Body != null)
                {
                    original.Body.mass = original.Mass;
                    original.Body.drag = original.Drag;
                    original.Body.angularDrag = original.AngularDrag;
                    original.Body.sleepThreshold = original.SleepThreshold;
                    original.Body.maxAngularVelocity = original.MaxAngularVelocity;
                    original.Body.maxLinearVelocity = original.MaxLinearVelocity;
                }
                if (original.Collider != null)
                    original.Collider.sharedMaterial = original.Material;
            }
            _originals.Clear();
            _pelvis = null;
        }

        // Moves each body onto its joint, parent first, so the simulation starts from a pose the
        // joints agree with. A joint that starts stretched is resolved in one violent step, which
        // is how a corpse that was not at rest explodes the moment the drag begins.
        internal static void SeatOnJoints(IEnumerable<CharacterJointSpawner> spawners)
        {
            if (spawners == null)
                return;
            bool moved = false;
            foreach (CharacterJointSpawner spawner in spawners)
            {
                CharacterJoint joint = spawner != null ? spawner.GetComponent<CharacterJoint>() : null;
                if (joint == null || joint.connectedBody == null)
                    continue;
                Vector3 gap = joint.connectedBody.transform.TransformPoint(joint.connectedAnchor) -
                    joint.transform.TransformPoint(joint.anchor);
                if (gap.sqrMagnitude < 1e-6f)
                    continue;
                joint.transform.position += gap;
                moved = true;
            }
            if (moved)
                Physics.SyncTransforms();
        }

        private static PhysicMaterial ContactMaterial()
        {
            if (_contactMaterial == null)
            {
                const float mu = GroundFriction / 9.81f;
                _contactMaterial = new PhysicMaterial("BodyDragging_CorpseContact")
                {
                    dynamicFriction = mu,
                    staticFriction = mu * 1.15f,
                    bounciness = 0f,
                    bounceCombine = PhysicMaterialCombine.Minimum,
                    frictionCombine = PhysicMaterialCombine.Maximum
                };
            }
            return _contactMaterial;
        }

        // EFT's prefab gives each arm segment 5.625 kg (both arms 22.5 kg, heavier than the legs or
        // the trunk), which yanks the shoulders around: human segment masses instead (~80 kg).
        private static float HumanSegmentMass(EBodyPartColliderType part)
        {
            switch (part)
            {
                case EBodyPartColliderType.Pelvis: return 14.0f;
                case EBodyPartColliderType.RibcageLow: return 12.0f;
                case EBodyPartColliderType.RibcageUp: return 14.0f;
                case EBodyPartColliderType.HeadCommon: return 4.5f;
                case EBodyPartColliderType.LeftUpperArm:
                case EBodyPartColliderType.RightUpperArm: return 2.4f;
                case EBodyPartColliderType.LeftForearm:
                case EBodyPartColliderType.RightForearm: return 1.6f;
                case EBodyPartColliderType.LeftThigh:
                case EBodyPartColliderType.RightThigh: return 9.0f;
                case EBodyPartColliderType.LeftCalf:
                case EBodyPartColliderType.RightCalf: return 4.5f;
                default: return 5.0f;
            }
        }
    }
}
