using UnityEngine;

namespace BodyDragging.Features
{
    // Where the dragger's hands are, from the pieces every peer already has: the dragger's synced
    // root position plus a handful of floats the dragger sends (yaw, hold distance, camera offset).
    // Pure math, matching Quaternion.Euler(0, yaw, 0): forward = (sin, cos), right = (cos, -sin).
    internal static class RemoteHoldMath
    {
        internal static Vector3 Target(Vector3 root, float yawDegrees, float localX, float localZ, float distance, float heightOffset)
        {
            float radians = yawDegrees * Mathf.Deg2Rad;
            float sin = Mathf.Sin(radians), cos = Mathf.Cos(radians);
            float z = localZ + distance;
            return new Vector3(root.x + localX * cos + z * sin, root.y + heightOffset, root.z - localX * sin + z * cos);
        }

        // inverse of the horizontal part: an offset in world space expressed in the yaw frame
        internal static void ToLocal(Vector3 offset, float yawDegrees, out float localX, out float localZ)
        {
            float radians = yawDegrees * Mathf.Deg2Rad;
            float sin = Mathf.Sin(radians), cos = Mathf.Cos(radians);
            localX = offset.x * cos - offset.z * sin;
            localZ = offset.x * sin + offset.z * cos;
        }
    }
}
