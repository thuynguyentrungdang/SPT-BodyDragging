using EFT;
using EFT.CameraControl;
using EFT.Interactive;
using UnityEngine;
using BodyDragging.Integration;

namespace BodyDragging.Features
{
    // Ported from TraumaCore's CorpseDragController (Apache-2.0, Hysocs): pulls the corpse's
    // chest rigidbody toward a point in front of the camera via a kinematic hand+joint tether
    // (ported from KeepMeAlive's BodyRagdoll; the original TraumaCore spring force has been
    // replaced - see NativeDragBody). Trauma-specific dependencies (Plugin.EnableCorpseDragging,
    // TraumaLog, wound inspection) are replaced with this mod's own config/log, and BodyDragSync
    // hooks let an optional Fika bridge mirror the drag to peers.
    //
    // This class is the local dragger: camera, target, hands, claim and network. The physics is
    // NativeDragBody, which every other peer also runs on its own copy of the body
    // (RemoteCorpseDragSim) from the hold parameters sent here.
    //
    // Grabbing while the corpse's own death ragdoll is still mid-collapse (joints not yet at an
    // equilibrium pose - confirmed via decompiled source + extensive diagnostic logging) reliably
    // explodes once dynamic joint physics + the break-force/solver overrides engage. A fully
    // kinematic rigid-carry avoids that but gives up real ragdoll motion entirely, which isn't
    // what's wanted here. Instead: wait for the corpse's own physics to actually go calm
    // (BeginSettling/TickSettling, polled by velocity - not EFT's own `_isPhysicsDone` flag, which
    // isn't guaranteed to ever flip under some hosting conditions) before engaging the real,
    // dynamic joint-driven drag.
    internal sealed partial class CorpseDragController : MonoBehaviour
    {
        private const float HeldDistanceBlendDuration = 0.25f;
        private const float PoseSendInterval = 1f / 15f;
        // how often the dragger's hold parameters (yaw, distance, camera offset) go to the other peers
        private const float HoldSendInterval = 0.1f;

        private static CorpseDragController _active;
        private NativeDragBody _body;
        private GamePlayerOwner _owner;
        private Corpse _corpse;
        private CorpseRagdoll _ragdoll;
        private Camera _camera;
        private float _grabDistance;
        private float _initialGrabDistance;
        private float _targetGrabDistance;
        private float _grabDistanceBlendElapsed;
        private float _cameraHeightAtCapture;
        private float _grabHeightAtCapture;
        private float _maximumTargetSeparation;
        private float _separationDuration;
        private bool _isSettling;
        private float _settleElapsed;
        private float _calmElapsed;
        private bool _isStopping;
        private bool _claimDenied;
        private Player _localPlayer;
        private float _nextPoseSendTime;
        private float _nextHoldSendTime;
        private uint _holdSequence;

        private bool _managed, _managedReady;
        private bool _nativeOwnershipStarted;
        // Rupture handed the corpse over (CorpseHandoffV1): same native drag, but HandoffLeases owns the token
        private bool _handoff;
        private ManagedDragStart _managedIdentity;
        private uint _managedStatusSequence, _managedInputSequence;
        private float _lastManagedStatus;
        private Vector3 _managedGripPoint;
        private float _managedBeganAt;

        internal static bool IsDragging(Corpse corpse) =>
            _active != null && _active._corpse == corpse;

        internal static bool HasActiveDrag => _active != null;
        internal static bool IsNativeDragging(Corpse corpse) => IsDragging(corpse) && !_active._managed;

        internal static void Begin(GamePlayerOwner owner, Corpse corpse)
        {
            if (!Plugin.Enabled.Value || owner == null || corpse?.Ragdoll == null)
                return;
            if (BodyDragSync.Active && BodyDragSync.IsProfileAlreadyDragged?.Invoke(corpse.PlayerProfileID) == true)
            {
                BodyDragLog.Info("[CorpseDrag] Corpse is already claimed by another peer");
                return;
            }
            if (_active != null)
                _active.StopDragging();

            Player localPlayer = GamePlayerOwner.MyPlayer;
            if (localPlayer == null)
                return;
            CorpseDragController controller = localPlayer.gameObject.AddComponent<CorpseDragController>();
            if (!controller.BeginSettling(owner, corpse))
            {
                Destroy(controller);
                return;
            }
            _active = controller;
            owner.ClearInteractionState();
            if (controller._managed)
            {
                if (BodyDragSync.ManagedStartRequested?.Invoke(controller._managedIdentity) != true) controller.StopDragging();
            }
            else if (BodyDragSync.Active)
                BodyDragSync.DragStartRequested?.Invoke(corpse.PlayerProfileID);
        }

        internal static void StopActiveDrag()
        {
            if (_active != null)
                _active.StopDragging();
        }

        // host denied the claim (another peer grabbed it first) - drop the local drag without
        // sending our own DragStopped, the corpse already belongs to someone else. Still runs
        // the normal OnDestroy cleanup/settlement handoff, just skips the network stop message
        internal static void OnDragDenied(string profileId)
        {
            if (_active != null && _active._corpse != null && _active._corpse.PlayerProfileID == profileId)
            {
                BodyDragLog.Info("[CorpseDrag] Claim denied by host, releasing locally");
                _active._claimDenied = true;
                Destroy(_active);
            }
        }

        // Claims the corpse and camera/validation state immediately (so nobody else can grab it
        // and the interaction prompt updates right away), but doesn't touch the ragdoll's physics
        // yet - TickSettling waits for it to actually go calm before EngageDrag runs.
        private bool BeginSettling(GamePlayerOwner owner, Corpse corpse)
        {
            _owner = owner;
            _corpse = corpse;
            _localPlayer = GamePlayerOwner.MyPlayer;
            _ragdoll = corpse.Ragdoll;
            _camera = CameraManager.Instance?.Camera ?? Camera.main;
            if (_camera == null || _ragdoll._owner == null)
                return false;
            if (!Plugin.AllowZombieOrBotCorpses.Value && corpse.IsZombieCorpse)
                return false;

            CorpseRoute route = RuptureDragProvider.Inspect(corpse, out ProviderInfo info);
            if (route == CorpseRoute.Blocked || (route == CorpseRoute.Managed && !info.IsAuthority && !BodyDragSync.Active)) return false;
            if (route == CorpseRoute.Managed)
            {
                _managed = true;
                _managedIdentity = new ManagedDragStart { Session = System.Guid.NewGuid().ToString("N"),
                    ProfileId = corpse.PlayerProfileID, DeathSequence = info.DeathSequence, DraggerProfileId = _localPlayer.ProfileId };
                _lastManagedStatus = _managedBeganAt = Time.realtimeSinceStartup;
                return true;
            }
            if (_ragdoll._rigidbodySpawners == null || _ragdoll._rigidbodySpawners.Length == 0) return false;

            CorpseRagdollSettlement.Cancel(corpse);
            CorpseRestDeclarer.Cancel(corpse);
            _handoff = route == CorpseRoute.Handoff;
            _body = new NativeDragBody(corpse, _handoff);
            _nativeOwnershipStarted = true;
            _isSettling = true;
            _settleElapsed = 0f;
            _calmElapsed = 0f;
            BodyDragLog.Info("[CorpseDrag] Corpse still settling, drag will engage once calm");
            return true;
        }

        // Polls the ragdoll's own (untouched) physics each frame until it's been calm for
        // RequiredCalmSeconds, or MaxSettleWaitSeconds elapses regardless.
        private void TickSettling(float deltaTime)
        {
            if (_corpse == null || _ragdoll?._rigidbodySpawners == null)
            {
                StopDragging();
                return;
            }

            _settleElapsed += deltaTime;
            _calmElapsed = _body.IsCalm() ? _calmElapsed + deltaTime : 0f;

            if (_calmElapsed < NativeDragBody.RequiredCalmSeconds && _settleElapsed < NativeDragBody.MaxSettleWaitSeconds)
                return;

            NativeDragBody.EngageResult result = EngageDrag();
            if (result == NativeDragBody.EngageResult.Pending)
                return;
            _isSettling = false;
            if (result == NativeDragBody.EngageResult.Failed)
            {
                BodyDragLog.Warning("[CorpseDrag] Could not engage drag after settling");
                StopDragging();
            }
        }

        // The real grab: runs only once the corpse has been confirmed calm by TickSettling.
        // Pending: handoff mode is still waiting for Rupture to release the corpse
        private NativeDragBody.EngageResult EngageDrag()
        {
            NativeDragBody.EngageResult result = _body.TryEngage();
            if (result != NativeDragBody.EngageResult.Engaged)
                return result;

            Vector3 worldPoint = _body.ChestCenter;
            float interactionRange = EFTHardSettings.Instance.LOOT_RAYCAST_DISTANCE + EFTHardSettings.Instance.BEHIND_CAST;
            Vector3 cameraToGrab = worldPoint - _camera.transform.position;
            _initialGrabDistance = Vector3.ProjectOnPlane(cameraToGrab, Vector3.up).magnitude;
            _targetGrabDistance = interactionRange * 0.5f * Plugin.HeldDistanceMultiplier.Value;
            _grabDistance = _initialGrabDistance;
            _grabDistanceBlendElapsed = 0f;
            _maximumTargetSeparation = interactionRange;
            _cameraHeightAtCapture = _camera.transform.position.y;
            _grabHeightAtCapture = worldPoint.y;
            DragHands.Begin(_localPlayer, this, _corpse.PlayerProfileID);
            BodyDragLog.Info(
                $"[CorpseDrag] Grabbed '{_body.GrabbedBody.name}' at {_initialGrabDistance:F2}m, blending to " +
                $"{_targetGrabDistance:F2}m inside EFT interaction range {interactionRange:F2}m");
            return NativeDragBody.EngageResult.Engaged;
        }

        internal static bool IsDraggedCollider(Collider collider)
        {
            RigidbodySpawner[] spawners = _active?._ragdoll?._rigidbodySpawners;
            Rigidbody body = collider != null ? collider.attachedRigidbody : null;
            if (spawners == null || body == null)
                return false;
            foreach (RigidbodySpawner spawner in spawners)
                if (spawner != null && spawner.Rigidbody == body)
                    return true;
            return false;
        }

        private void TickDrag(float deltaTime)
        {
            if (_body == null || !_body.IsEngaged || _camera == null || _corpse == null)
            {
                StopDragging();
                return;
            }
            if (_handoff && !HandoffLeases.Holds(_corpse))
            {
                // Rupture took the corpse back (lease expired, raid reset): nothing left to drag
                BodyDragLog.Warning("[CorpseDrag] Handoff lease lost, releasing corpse");
                StopDragging();
                return;
            }

            Transform cameraTransform = _camera.transform;
            Vector3 cameraPosition = cameraTransform.position;
            float yaw = cameraTransform.eulerAngles.y;
            Vector3 worldPoint = _body.ChestCenter;
            _grabDistanceBlendElapsed += deltaTime;
            float distanceBlend = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(_grabDistanceBlendElapsed / HeldDistanceBlendDuration));
            _grabDistance = Mathf.Lerp(_initialGrabDistance, _targetGrabDistance, distanceBlend);
            Vector3 horizontalForward = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
            Vector3 target = cameraPosition + horizontalForward * _grabDistance;
            target.y = _grabHeightAtCapture + (cameraPosition.y - _cameraHeightAtCapture);
            if ((worldPoint - cameraPosition).sqrMagnitude > _maximumTargetSeparation * _maximumTargetSeparation)
            {
                _separationDuration += deltaTime;
                if (_separationDuration >= Plugin.MaximumSeparationSeconds.Value)
                {
                    BodyDragLog.Info("[CorpseDrag] Released corpse after remaining outside the drag range too long");
                    StopDragging();
                    return;
                }
            }
            else
            {
                _separationDuration = 0f;
            }

            _body.Tick(target, deltaTime);
            SendHoldIfDue(cameraPosition, yaw, target);
        }

        // The other peers simulate their own copy of the body; this is all they need of the hold
        // (they already have this player's position from normal Fika movement sync).
        private void SendHoldIfDue(Vector3 cameraPosition, float yaw, Vector3 target)
        {
            if (!BodyDragSync.Active || Time.unscaledTime < _nextHoldSendTime)
                return;
            _nextHoldSendTime = Time.unscaledTime + HoldSendInterval;
            Vector3 root = _localPlayer.Position;
            RemoteHoldMath.ToLocal(cameraPosition - root, yaw, out float localX, out float localZ);
            BodyDragSync.HoldSent?.Invoke(new BodyDragHold
            {
                ProfileId = _corpse.PlayerProfileID,
                Sequence = ++_holdSequence,
                Yaw = yaw,
                Distance = _grabDistance,
                LocalX = localX,
                LocalZ = localZ,
                HeightOffset = target.y - root.y
            });
        }

        private void Update()
        {
            if (_localPlayer?.MovementContext != null)
                _localPlayer.MovementContext.EnableSprint(false);
            if (_active == this)
                DragHands.Tick();
            if (_managed)
                TickManaged(Time.unscaledDeltaTime);
            else if (_isSettling)
                TickSettling(Time.deltaTime);
            else
                TickDrag(Time.deltaTime);
        }

        private void StopDragging()
        {
            if (_isStopping)
                return;
            _isStopping = true;
            if (_active == this)
                _active = null;
            if (_owner != null)
                _owner.ClearInteractionState();
            Destroy(this);
        }

        private void OnDestroy()
        {
            if (_managed)
            {
                try
                {
                    if (!_claimDenied) BodyDragSync.ManagedEndRequested?.Invoke(new ManagedDragEnd {
                        Session = _managedIdentity.Session, ProfileId = _managedIdentity.ProfileId, DeathSequence = _managedIdentity.DeathSequence });
                }
                catch (System.Exception exception) { BodyDragLog.Warning("[Rupture] End intent send failed: " + exception.Message); }
                if (_localPlayer != null) _localPlayer.UpdateSpeedLimitByHealth();
                DragHands.End(this);
                if (_active == this) _active = null;
                return;
            }
            if (!_nativeOwnershipStarted)
            {
                if (_active == this) _active = null;
                return;
            }
            bool engaged = _body != null && _body.IsEngaged;
            if (_body != null)
                _body.Release();
            if (_localPlayer != null)
                _localPlayer.UpdateSpeedLimitByHealth();
            DragHands.End(this);
            // handoff: this peer settles the body and declares where it lies (a denied claim waits for the winner's
            // pose instead), then HandoffLeases gives the corpse back to Rupture with one Adopt
            if (_handoff && _corpse != null)
                HandoffLeases.BeginDrop(_corpse, !_claimDenied, HandoffLeases.GraphicalRestWaitSeconds);
            if (_corpse != null && BodyDragSync.Active && !_claimDenied)
            {
                BodyDragSync.DragStoppedLocally?.Invoke(_corpse.PlayerProfileID);
                // every peer settles its own copy; once this one has, the final pose goes out so they all match it
                if (engaged && !_handoff)
                    CorpseRestDeclarer.Begin(_corpse);
            }
            if (_active == this)
                _active = null;
            BodyDragLog.Info(_handoff ? "[CorpseDrag] Released corpse, settling before handing it back to Rupture" : "[CorpseDrag] Released corpse to EFT settling cycle");
        }
    }
}
