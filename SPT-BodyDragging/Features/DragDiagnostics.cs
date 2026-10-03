using System.Collections.Generic;
using EFT.Interactive;
using UnityEngine;

namespace BodyDragging.Features
{
    // TEMP: first-drag-not-ragdolling investigation. Always logs (bypasses Debug Logging) with a
    // [DragDiag] tag. Delete this file and its call sites once the cause is confirmed.
    internal static class DragDiagnostics
    {
        private const float ProbeSeconds = 4f;
        private const float ProbeInterval = 0.5f;

        internal static void LogActivation(CorpseRagdoll ragdoll, bool wasPhysicsDone, bool hadMissingBody, bool putToSleep, string path)
        {
            Log($"activate path={path} wasPhysicsDone={wasPhysicsDone} hadMissingBody={hadMissingBody} " +
                $"putToSleep={putToSleep} nowPhysicsDone={ragdoll._isPhysicsDone}");
        }

        internal static void LogState(string label, CorpseRagdoll ragdoll, IReadOnlyList<Rigidbody> bodies,
            Rigidbody chest, Vector3 chestStart, Rigidbody hand, RagdollJointStability joints)
        {
            int kinematic = 0, sleeping = 0, supported = 0, missing = 0;
            foreach (Rigidbody body in bodies)
            {
                if (body == null) { missing++; continue; }
                if (body.isKinematic) kinematic++;
                if (body.IsSleeping()) sleeping++;
                if (IsSupported(body)) supported++;
            }

            string chestInfo = chest == null ? "chest=null" :
                $"chestKin={chest.isKinematic} chestSleep={chest.IsSleeping()} chestVel={chest.velocity.magnitude:F2} " +
                $"chestMoved={(chest.position - chestStart).magnitude:F2} handGap={(hand != null ? (hand.position - chest.position).magnitude : -1f):F2}";

            Log($"{label} bodies={bodies.Count} kinematic={kinematic} sleeping={sleeping} supported={supported} missing={missing} " +
                $"{chestInfo} joints={joints.JointCount} snags={joints.SnagEvents} released={joints.ReleasedCount()} " +
                $"physicsDone={ragdoll._isPhysicsDone} canSimulate={PhysicsExtensions.Simulation.CanRunSimulate} " +
                $"updCtrl={PhysicsExtensions.UpdateController._enabled} supportedTotal={PhysicsExtensions.UpdateController._rigidbodies.Count} " +
                $"simMode={Physics.simulationMode} autoSync={Physics.autoSyncTransforms}");
        }

        internal static bool ShouldProbe(float elapsed, ref float nextProbe)
        {
            if (elapsed > ProbeSeconds || elapsed < nextProbe)
                return false;
            nextProbe = elapsed + ProbeInterval;
            return true;
        }

        private static bool IsSupported(Rigidbody body)
        {
            foreach (PhysicsExtensions.UpdateController.RigidbodyData data in PhysicsExtensions.UpdateController._rigidbodies)
                if (data.rigidbody == body)
                    return true;
            return false;
        }

        private static void Log(string message) => Plugin.Log?.LogInfo("[DragDiag] " + message);
    }
}
