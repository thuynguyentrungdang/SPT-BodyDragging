using UnityEngine;

namespace BodyDragging.Features
{
    // Limb-follow spring-damper for the native drag. The force is applied once per physics step,
    // so the explicit form (k*x - c*v) flips and amplifies velocity whenever c*dt > 2, i.e. below
    // ~70 FPS with the default strength. The backward-Euler form is stable at any step length
    // (same formula the Rupture route uses in ManagedDragDrive).
    internal static class LimbAssistMath
    {
        internal const float MinimumStep = 1f / 240f;
        internal const float MaximumStep = 0.1f;

        internal static Vector3 Acceleration(Vector3 toTarget, Vector3 velocity, float spring, float step, float maximumAcceleration)
        {
            float damping = 2.2f * Mathf.Sqrt(Mathf.Max(spring, 0.01f));
            float denominator = 1f + damping * step + spring * step * step;
            Vector3 acceleration = (toTarget * spring - velocity * (damping + spring * step)) / denominator;
            return Vector3.ClampMagnitude(acceleration, maximumAcceleration);
        }
    }
}
