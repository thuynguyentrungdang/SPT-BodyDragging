using System;
using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace BodyDragging.Features
{
    // Ported from TraumaCore's RagdollJointStability (Apache-2.0, Hysocs). Temporarily makes the
    // ragdoll's character joints unbreakable and raises solver iterations while dragging, and
    // repairs/clamps joints that stretch too far or fold the elbow past a believable angle.
    internal sealed class RagdollJointStability
    {
        private const float MaximumAnchorSeparation = 0.015f;
        private const float RepairAnchorSeparation = 0.025f;
        // Ported technique from KeepMeAlive's BodyRagdoll: a joint stretched this far is a snag
        // (limb wedged in geometry), not ordinary drag slack - closing the gap with a partial pull
        // just fights the collision every tick. Instead close it fully and let the limb's own
        // colliders pass through the world briefly so it can slide free, then re-enable them.
        private const float SnagSeparation = 0.08f;
        private const float SnagReleaseSeconds = 0.35f;
        // A limb wedged in a wall/corner can start with a separation of decimeters to meters -
        // closing that in one write (the old behavior) is a teleport. Cap the correction to a
        // speed instead, scaled by deltaTime so it doesn't resolve faster at higher framerate.
        private const float MaxRepairSpeed = 6f;
        private const float MaximumElbowFoldAngle = 135f;
        private const int MinimumSolverIterations = 12;
        private const int MinimumSolverVelocityIterations = 4;

        private sealed class JointState
        {
            internal CharacterJoint Joint;
            internal bool IsProjectionEnabled;
            internal float ProjectionDistance;
            internal float ProjectionAngle;
            internal bool IsPreprocessingEnabled;
            internal float BreakForce;
            internal float BreakTorque;
            internal Rigidbody Body;
            internal bool IsElbow;
            internal int SolverIterations;
            internal int SolverVelocityIterations;
            internal Collider[] BodyColliders;
            internal bool IsSnagReleased;
            internal float SnagReleaseUntil;
        }

        private readonly List<JointState> _jointStates = new();

        // TEMP diagnostics (DragDiagnostics) - remove with it
        internal int SnagEvents;
        internal int JointCount => _jointStates.Count;
        internal int ReleasedCount()
        {
            int count = 0;
            foreach (JointState state in _jointStates)
                if (state.IsSnagReleased)
                    count++;
            return count;
        }

        internal void Capture(IEnumerable<CharacterJointSpawner> spawners)
        {
            Restore();
            if (spawners == null)
                return;
            foreach (CharacterJointSpawner spawner in spawners)
            {
                CharacterJoint joint = spawner?.GetComponent<CharacterJoint>();
                if (joint == null)
                    continue;
                Rigidbody body = joint.GetComponent<Rigidbody>();
                _jointStates.Add(new JointState
                {
                    Joint = joint,
                    IsProjectionEnabled = joint.enableProjection,
                    ProjectionDistance = joint.projectionDistance,
                    ProjectionAngle = joint.projectionAngle,
                    IsPreprocessingEnabled = joint.enablePreprocessing,
                    BreakForce = joint.breakForce,
                    BreakTorque = joint.breakTorque,
                    Body = body,
                    IsElbow = IsForearmJoint(spawner, joint, body),
                    SolverIterations = body != null ? body.solverIterations : 0,
                    SolverVelocityIterations = body != null ? body.solverVelocityIterations : 0,
                    BodyColliders = CollectSolidColliders(body)
                });
                joint.breakForce = float.PositiveInfinity;
                joint.breakTorque = float.PositiveInfinity;
                if (body != null)
                {
                    body.solverIterations = Mathf.Max(body.solverIterations, MinimumSolverIterations);
                    body.solverVelocityIterations = Mathf.Max(body.solverVelocityIterations, MinimumSolverVelocityIterations);
                }
            }
        }

        internal void RepairExcessiveSeparation(float deltaTime)
        {
            float maxStep = MaxRepairSpeed * deltaTime;
            for (int i = 0; i < _jointStates.Count; i++)
            {
                JointState state = _jointStates[i];
                CharacterJoint joint = state.Joint;
                Rigidbody body = state.Body;
                if (joint == null || body == null)
                    continue;
                RestoreSnagReleaseIfDue(state);
                if (state.IsElbow)
                    ClampElbowFold(joint, body);
                Vector3 bodyAnchor = joint.transform.TransformPoint(joint.anchor);
                Vector3 connectedAnchor = joint.connectedBody != null
                    ? joint.connectedBody.transform.TransformPoint(joint.connectedAnchor)
                    : joint.connectedAnchor;
                Vector3 separation = connectedAnchor - bodyAnchor;
                float distance = separation.magnitude;
                if (distance <= RepairAnchorSeparation)
                    continue;

                // Likely wedged in geometry rather than just lagging - let this limb's colliders
                // pass through the world briefly so the capped step below can slide it free
                // instead of fighting whatever it is stuck on every tick
                if (distance > SnagSeparation)
                {
                    ReleaseFromWorld(state);
                    SnagEvents++;
                }

                Vector3 desired = separation * ((distance - MaximumAnchorSeparation) / distance);
                body.position += Vector3.ClampMagnitude(desired, maxStep);
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
                // next joint in the chain may read this body's transform as its connectedBody -
                // make sure that read sees the write just made, not last frame's position
                Physics.SyncTransforms();
            }
        }

        // Only the body's own colliders: moving body.position doesn't carry child bodies (each
        // has its own joint/state), and children may hold attached gear colliders. Already-trigger
        // colliders are skipped so the snag release can always restore isTrigger to false.
        private static Collider[] CollectSolidColliders(Rigidbody body)
        {
            if (body == null)
                return null;
            Collider[] own = body.GetComponents<Collider>();
            List<Collider> solid = new(own.Length);
            foreach (Collider collider in own)
                if (collider != null && !collider.isTrigger)
                    solid.Add(collider);
            return solid.ToArray();
        }

        private static void ReleaseFromWorld(JointState state)
        {
            state.IsSnagReleased = true;
            state.SnagReleaseUntil = Time.time + SnagReleaseSeconds;
            if (state.BodyColliders == null)
                return;
            foreach (Collider collider in state.BodyColliders)
                if (collider != null)
                    collider.isTrigger = true;
        }

        private static void RestoreSnagReleaseIfDue(JointState state)
        {
            if (!state.IsSnagReleased || Time.time < state.SnagReleaseUntil)
                return;
            state.IsSnagReleased = false;
            if (state.BodyColliders == null)
                return;
            foreach (Collider collider in state.BodyColliders)
                if (collider != null)
                    collider.isTrigger = false;
        }

        private static bool IsForearmJoint(CharacterJointSpawner spawner, CharacterJoint joint, Rigidbody body)
        {
            string names = $"{spawner?.name} {joint?.name} {body?.name}";
            return names.IndexOf("forearm", StringComparison.OrdinalIgnoreCase) >= 0 ||
                names.IndexOf("lowerarm", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void ClampElbowFold(CharacterJoint joint, Rigidbody forearm)
        {
            Rigidbody upperArm = joint.connectedBody;
            if (upperArm == null)
                return;

            Vector3 elbow = joint.transform.TransformPoint(joint.anchor);
            Vector3 upperDirection = elbow - upperArm.worldCenterOfMass;
            Vector3 forearmDirection = forearm.worldCenterOfMass - elbow;
            if (upperDirection.sqrMagnitude < 0.0001f || forearmDirection.sqrMagnitude < 0.0001f)
                return;

            upperDirection.Normalize();
            forearmDirection.Normalize();
            if (Vector3.Angle(upperDirection, forearmDirection) <= MaximumElbowFoldAngle)
                return;

            Vector3 clampedDirection = Vector3.RotateTowards(upperDirection, forearmDirection,
                MaximumElbowFoldAngle * Mathf.Deg2Rad, 0f);
            Quaternion correction = Quaternion.FromToRotation(forearmDirection, clampedDirection);
            forearm.position = elbow + correction * (forearm.position - elbow);
            forearm.rotation = correction * forearm.rotation;
            forearm.velocity = Vector3.zero;
            forearm.angularVelocity = Vector3.zero;
        }

        internal void Restore()
        {
            for (int i = 0; i < _jointStates.Count; i++)
            {
                JointState state = _jointStates[i];
                if (state.Joint == null)
                    continue;
                state.Joint.enableProjection = state.IsProjectionEnabled;
                state.Joint.projectionDistance = state.ProjectionDistance;
                state.Joint.projectionAngle = state.ProjectionAngle;
                state.Joint.enablePreprocessing = state.IsPreprocessingEnabled;
                state.Joint.breakForce = state.BreakForce;
                state.Joint.breakTorque = state.BreakTorque;
                if (state.Body != null)
                {
                    state.Body.solverIterations = state.SolverIterations;
                    state.Body.solverVelocityIterations = state.SolverVelocityIterations;
                }
                if (state.IsSnagReleased && state.BodyColliders != null)
                    foreach (Collider collider in state.BodyColliders)
                        if (collider != null)
                            collider.isTrigger = false;
            }
            _jointStates.Clear();
        }
    }
}
