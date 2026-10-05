using BodyDragging.Integration;
using UnityEngine;

namespace BodyDragging.Features
{
    internal sealed partial class CorpseDragController
    {
        internal static void OnManagedStatus(ManagedDragStatus status)
        {
            CorpseDragController controller = _active;
            if (controller == null || !controller._managed || !ManagedIntentBuffer.Same(controller._managedIdentity.Session,
                controller._managedIdentity.ProfileId, controller._managedIdentity.DeathSequence, status.Session, status.ProfileId, status.DeathSequence) ||
                status.Sequence == 0 || status.Sequence <= controller._managedStatusSequence || !ManagedIntentBuffer.Finite(status.GripPoint)) return;
            controller._managedStatusSequence = status.Sequence;
            controller._lastManagedStatus = Time.realtimeSinceStartup;
            controller._managedGripPoint = status.GripPoint;
            if (status.Stage == ManagedDragStage.Releasing || status.Stage == ManagedDragStage.Closed || status.Stage == ManagedDragStage.Failed)
            {
                controller._claimDenied = true; // authority already closed; no second end/pose
                controller.StopDragging();
                return;
            }
            if (status.Stage != ManagedDragStage.Held || controller._managedReady) return;
            if (controller._camera == null) { controller.StopDragging(); return; }
            Vector3 cameraPosition = controller._camera.transform.position;
            float range = EFTHardSettings.Instance.LOOT_RAYCAST_DISTANCE + EFTHardSettings.Instance.BEHIND_CAST;
            controller._initialGrabDistance = Vector3.ProjectOnPlane(status.GripPoint - cameraPosition, Vector3.up).magnitude;
            controller._targetGrabDistance = range * .5f * Plugin.HeldDistanceMultiplier.Value;
            controller._maximumTargetSeparation = range;
            controller._cameraHeightAtCapture = cameraPosition.y;
            controller._grabHeightAtCapture = status.GripPoint.y;
            controller._managedReady = true;
            BodyDragLog.Info($"[Rupture] timing client: Held status received {(Time.realtimeSinceStartup - controller._managedBeganAt) * 1000f:F0}ms after grab");
            if (Plugin.UnequipHandsWhileDragging.Value) controller.UnequipLocalPlayerHands();
            BodyDragLog.Info("[Rupture] Authority drag engaged");
        }

        private void TickManaged(float deltaTime)
        {
            if (!Plugin.Enabled.Value || _camera == null || _corpse == null || _localPlayer == null ||
                _localPlayer.HealthController?.IsAlive == false || Time.realtimeSinceStartup - _lastManagedStatus >= 5f)
            {
                StopDragging();
                return;
            }
            Vector3 target = Vector3.zero;
            float holdDistance = 0, localX = 0, localZ = 0, heightOffset = 0, yaw = 0;
            if (_managedReady)
            {
                Transform camera = _camera.transform;
                Vector3 position = camera.position;
                _grabDistanceBlendElapsed += deltaTime;
                float blend = Mathf.SmoothStep(0, 1, Mathf.Clamp01(_grabDistanceBlendElapsed / HeldDistanceBlendDuration));
                float distance = Mathf.Lerp(_initialGrabDistance, _targetGrabDistance, blend);
                yaw = camera.eulerAngles.y;
                target = position + Quaternion.Euler(0, yaw, 0) * Vector3.forward * distance;
                target.y = _grabHeightAtCapture + position.y - _cameraHeightAtCapture;
                Vector3 root = _localPlayer.Position;
                Vector3 local = Quaternion.Euler(0, -yaw, 0) * new Vector3(position.x - root.x, 0, position.z - root.z);
                holdDistance = distance; localX = local.x; localZ = local.z; heightOffset = target.y - root.y;
                if ((_managedGripPoint - position).sqrMagnitude > _maximumTargetSeparation * _maximumTargetSeparation)
                {
                    _separationDuration += deltaTime;
                    if (_separationDuration >= Plugin.MaximumSeparationSeconds.Value) { StopDragging(); return; }
                }
                else _separationDuration = 0;
            }
            bool localAuthority = !BodyDragSync.Active || BodyDragSync.IsHost;
            if (!localAuthority && Time.unscaledTime < _nextPoseSendTime) return;
            if (_managedInputSequence == uint.MaxValue) { StopDragging(); return; }
            _nextPoseSendTime = Time.unscaledTime + PoseSendInterval;
            BodyDragSync.ManagedInputSent?.Invoke(new ManagedDragInput {
                Session = _managedIdentity.Session, ProfileId = _managedIdentity.ProfileId, DeathSequence = _managedIdentity.DeathSequence,
                Sequence = ++_managedInputSequence, HasTarget = _managedReady, Target = target,
                Distance = holdDistance, LocalX = localX, LocalZ = localZ, HeightOffset = heightOffset, Yaw = yaw });
        }
    }
}
