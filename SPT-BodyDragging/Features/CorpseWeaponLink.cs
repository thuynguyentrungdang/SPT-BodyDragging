using System.Collections.Generic;
using EFT.Interactive;
using UnityEngine;

namespace BodyDragging.Features
{
    // Ported from TraumaCore's CorpseWeaponLink (Apache-2.0, Hysocs). Detaches the corpse's held
    // weapon joint and ignores weapon<->body collisions for the duration of the drag so the
    // weapon doesn't fling the ragdoll around.
    internal static class CorpseWeaponLink
    {
        internal sealed class DetachedWeapon
        {
            private readonly List<(Collider Weapon, Collider Body)> _ignoredColliderPairs = new();

            internal void CaptureIgnoredCollision(Collider weaponCollider, Collider bodyCollider)
            {
                _ignoredColliderPairs.Add((weaponCollider, bodyCollider));
            }

            internal void RestoreCollisions()
            {
                foreach ((Collider weapon, Collider body) in _ignoredColliderPairs)
                {
                    if (weapon != null && body != null)
                        Physics.IgnoreCollision(weapon, body, false);
                }
                _ignoredColliderPairs.Clear();
            }
        }

        internal static DetachedWeapon Detach(CorpseRagdoll ragdoll)
        {
            DetachedWeapon detachedWeapon = new DetachedWeapon();
            if (ragdoll == null)
                return detachedWeapon;
            if (ragdoll._weaponJoint != null)
            {
                Joint weaponJoint = ragdoll._weaponJoint;
                ragdoll._weaponJoint = null;
                Object.DestroyImmediate(weaponJoint);
            }
            IgnoreWeaponCorpseCollision(ragdoll, detachedWeapon);
            BodyDragLog.Info("[CorpseDrag] Detached weapon and ignored corpse collision");
            return detachedWeapon;
        }

        private static void IgnoreWeaponCorpseCollision(CorpseRagdoll ragdoll, DetachedWeapon detachedWeapon)
        {
            Rigidbody weapon = ragdoll._weaponRigidbody;
            if (weapon == null || ragdoll._rigidbodySpawners == null)
                return;

            Collider[] weaponColliders = weapon.GetComponentsInChildren<Collider>(true);
            for (int bodyIndex = 0; bodyIndex < ragdoll._rigidbodySpawners.Length; bodyIndex++)
            {
                RigidbodySpawner spawner = ragdoll._rigidbodySpawners[bodyIndex];
                if (spawner == null)
                    continue;
                Collider[] bodyColliders = spawner.GetComponentsInChildren<Collider>(true);
                for (int weaponIndex = 0; weaponIndex < weaponColliders.Length; weaponIndex++)
                {
                    Collider weaponCollider = weaponColliders[weaponIndex];
                    if (weaponCollider == null)
                        continue;
                    for (int colliderIndex = 0; colliderIndex < bodyColliders.Length; colliderIndex++)
                    {
                        Collider bodyCollider = bodyColliders[colliderIndex];
                        if (bodyCollider != null && bodyCollider != weaponCollider &&
                            !Physics.GetIgnoreCollision(weaponCollider, bodyCollider))
                        {
                            Physics.IgnoreCollision(weaponCollider, bodyCollider, true);
                            detachedWeapon.CaptureIgnoredCollision(weaponCollider, bodyCollider);
                        }
                    }
                }
            }
        }
    }
}
