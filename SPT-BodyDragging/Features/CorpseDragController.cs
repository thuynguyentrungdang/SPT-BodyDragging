using System.Collections.Generic;
using System.Linq;
using EFT;
using EFT.CameraControl;
using EFT.Interactive;
using EFT.InventoryLogic;
using UnityEngine;
using BodyDragging.Integration;

namespace BodyDragging.Features
{
    // Ported from TraumaCore's CorpseDragController (Apache-2.0, Hysocs): pulls the corpse's
    // chest rigidbody toward a point in front of the camera via a kinematic hand+joint tether
    // (ported from KeepMeAlive's BodyRagdoll; the original TraumaCore spring force has been
    // replaced - see CreateHand). Trauma-specific dependencies (Plugin.EnableCorpseDragging,
    // TraumaLog, wound inspection) are replaced with this mod's own config/log, and BodyDragSync
    // hooks let an optional Fika bridge mirror the drag to peers.
    //
    // Grabbing while the corpse's own death ragdoll is still mid-collapse (joints not yet at an
    // equilibrium pose - confirmed via decompiled source + extensive diagnostic logging) reliably
    // explodes once dynamic joint physics + this class's break-force/solver overrides engage. A
    // fully kinematic rigid-carry avoids that but gives up real ragdoll motion entirely, which
    // isn't what's wanted here. Instead: wait for the corpse's own physics to actually go calm
    // (BeginSettling/TickSettling, polled by velocity - not EFT's own `_isPhysicsDone` flag, which
    // isn't guaranteed to ever flip under some hosting conditions) before engaging the real,
    // dynamic joint-driven drag below. Matches why KeepMeAlive's own joint-tether ragdoll (ported
    // from here originally) never explodes: it only ever ragdolls a living player from a calm
    // pose, never a body that just absorbed a death impulse mid-collapse.
    internal sealed partial class CorpseDragController : MonoBehaviour
    {
        private const float HeldDistanceBlendDuration = 0.25f;
        private const float EmptyHandsRetryDelay = 0.25f;
        private const float EmptyHandsRequestTimeout = 1.5f;
        private const float PoseSendInterval = 1f / 15f;        // Below this, a body counts as settled rather than still actively falling/flailing.
        private const float SettleVelocityThreshold = 0.3f;
        private const float SettleAngularVelocityThreshold = 1f;
        // Calm has to hold for a short stretch, not just one lucky frame mid-flail.
        private const float RequiredCalmSeconds = 0.3f;
        // Safety cap so a corpse that never reads as calm (edge case) doesn't block the grab
        // forever - accept the residual risk after this long rather than brick dragging entirely,
        // which is what gating on `_isPhysicsDone` directly did.
        private const float MaxSettleWaitSeconds = 3f;

        private sealed class BodyState
        {
            internal Rigidbody Body;
            internal RigidbodyConstraints Constraints;
            internal Vector3 InitialWorldPosition;
            internal CollisionDetectionMode OriginalCollisionMode;
        }

        private static CorpseDragController _active;
        private readonly List<BodyState> _bodyStates = new();
        private readonly RagdollJointStability _jointStability = new();
        private GamePlayerOwner _owner;
        private Corpse _corpse;
        private CorpseRagdoll _ragdoll;
        private Camera _camera;
        private Rigidbody _grabbedBody;
        private float _grabDistance;
        private float _initialGrabDistance;
        private float _targetGrabDistance;
        private float _grabDistanceBlendElapsed;
        private float _cameraHeightAtCapture;
        private float _grabHeightAtCapture;
        private float _maximumTargetSeparation;
        private float _separationDuration;
        private bool _originalPutToSleep;
        private bool _isSettling;
        private float _settleElapsed;
        private float _calmElapsed;
        private bool _isStopping;
        private bool _claimDenied;
        private CorpseWeaponLink.DetachedWeapon _detachedWeapon;
        private Player _localPlayer;
        private Item _previousHandsItem;
        private bool _shouldRestoreHands;
        private bool _shouldEnforceEmptyHands;
        private bool _isEmptyHandsRequestPending;
        private float _nextEmptyHandsRequestTime;
        private float _emptyHandsRequestExpires;
        private float _nextPoseSendTime;
        private uint _poseSequence;
        private Vector3 _grabbedBodyPositionAtCapture;
        private GameObject _handObject;
        private Rigidbody _handBody;
        private ConfigurableJoint _handJoint;
        private Vector3 _lastHandTarget;

        private bool _managed, _managedReady;
        private bool _nativeOwnershipStarted;
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
                    ProfileId = corpse.PlayerProfileID, DeathSequence = info.DeathSequence };
                _lastManagedStatus = _managedBeganAt = Time.realtimeSinceStartup;
                return true;
            }
            if (_ragdoll._rigidbodySpawners == null || _ragdoll._rigidbodySpawners.Length == 0) return false;

            CorpseRagdollSettlement.Cancel(corpse);
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
            _calmElapsed = IsRagdollCalm() ? _calmElapsed + deltaTime : 0f;

            if (_calmElapsed < RequiredCalmSeconds && _settleElapsed < MaxSettleWaitSeconds)
                return;

            _isSettling = false;
            if (!EngageDrag())
            {
                BodyDragLog.Warning("[CorpseDrag] Could not engage drag after settling");
                StopDragging();
            }
        }

        private bool IsRagdollCalm()
        {
            float velocitySq = SettleVelocityThreshold * SettleVelocityThreshold;
            float angularVelocitySq = SettleAngularVelocityThreshold * SettleAngularVelocityThreshold;
            foreach (RigidbodySpawner spawner in _ragdoll._rigidbodySpawners)
            {
                Rigidbody body = spawner?.Rigidbody;
                if (body == null || body.isKinematic)
                    continue;
                if (body.velocity.sqrMagnitude > velocitySq || body.angularVelocity.sqrMagnitude > angularVelocitySq)
                    return false;
            }
            return true;
        }

        // The real grab: runs only once the corpse has been confirmed calm by TickSettling.
        private bool EngageDrag()
        {
            Dictionary<RigidbodySpawner, (Vector3 Position, Quaternion Rotation)> poses =
                _ragdoll._rigidbodySpawners
                    .Where(spawner => spawner != null)
                    .ToDictionary(spawner => spawner, spawner => (spawner.transform.position, spawner.transform.rotation));
            _detachedWeapon = CorpseWeaponLink.Detach(_ragdoll);
            ActivateRagdoll();
            _jointStability.Capture(_ragdoll._jointSpawners);
            foreach (KeyValuePair<RigidbodySpawner, (Vector3 Position, Quaternion Rotation)> pose in poses)
            {
                Rigidbody body = pose.Key.Rigidbody;
                if (body == null)
                    continue;
                pose.Key.transform.SetPositionAndRotation(pose.Value.Position, pose.Value.Rotation);
                body.position = pose.Value.Position;
                body.rotation = pose.Value.Rotation;
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
                _bodyStates.Add(new BodyState
                {
                    Body = body,
                    Constraints = body.constraints,
                    OriginalCollisionMode = body.collisionDetectionMode
                });
                body.constraints = RigidbodyConstraints.None;
                body.isKinematic = false;
                // a limb whipped around by a fast drag is small/fast enough to tunnel through thin
                // floors/stairs at the default discrete detection mode
                body.collisionDetectionMode = CollisionDetectionMode.Continuous;
                body.WakeUp();
                EnsurePhysicsStepped(body);
            }
            Physics.SyncTransforms();
            if (!FindChestBody(out _grabbedBody, out Vector3 worldPoint))
                return false;

            _grabbedBodyPositionAtCapture = _grabbedBody.position;
            foreach (BodyState state in _bodyStates)
                if (state.Body != null)
                    state.InitialWorldPosition = state.Body.position;

            float interactionRange = EFTHardSettings.Instance.LOOT_RAYCAST_DISTANCE + EFTHardSettings.Instance.BEHIND_CAST;
            Vector3 cameraToGrab = worldPoint - _camera.transform.position;
            _initialGrabDistance = Vector3.ProjectOnPlane(cameraToGrab, Vector3.up).magnitude;
            _targetGrabDistance = interactionRange * 0.5f * Plugin.HeldDistanceMultiplier.Value;
            _grabDistance = _initialGrabDistance;
            _grabDistanceBlendElapsed = 0f;
            _maximumTargetSeparation = interactionRange;
            _cameraHeightAtCapture = _camera.transform.position.y;
            _grabHeightAtCapture = worldPoint.y;
            CreateHand(worldPoint);
            LogDiagState("capture");
            if (Plugin.UnequipHandsWhileDragging.Value)
                UnequipLocalPlayerHands();
            BodyDragLog.Info(
                $"[CorpseDrag] Grabbed '{_grabbedBody.name}' at {_initialGrabDistance:F2}m, blending to " +
                $"{_targetGrabDistance:F2}m inside EFT interaction range {interactionRange:F2}m");
            return true;
        }

        // EFT runs Physics.Simulate itself, and only while at least one rigidbody is registered
        // with its UpdateController. A freshly killed corpse's bodies can be unregistered (seen in
        // logs: 0 supported, simulation off) - dynamic but never stepped, so the drag does nothing.
        // CorpseRagdoll.Start() registers them, which is why a second grab used to work.
        // SupportRigidbody doesn't dedupe; UnsupportRigidbody removes one entry, so add once.
        private static void EnsurePhysicsStepped(Rigidbody body)
        {
            foreach (PhysicsExtensions.UpdateController.RigidbodyData data in PhysicsExtensions.UpdateController._rigidbodies)
                if (data.rigidbody == body)
                    return;
            PhysicsExtensions.UpdateController.SupportRigidbody(body, 0f);
        }

        private void UnequipLocalPlayerHands()
        {
            if (_localPlayer == null)
                return;

            if (!_localPlayer.HandsIsEmpty)
            {
                _localPlayer.TrySaveLastItemInHands();
                _previousHandsItem = _localPlayer.LastEquippedWeaponOrKnifeItem;
                _shouldRestoreHands = _previousHandsItem != null;
            }
            _shouldEnforceEmptyHands = true;
            RequestEmptyHands();
        }

        private void RequestEmptyHands()
        {
            if (!_shouldEnforceEmptyHands || _localPlayer == null ||
                _localPlayer.HandsIsEmpty || Time.unscaledTime < _nextEmptyHandsRequestTime)
                return;
            if (_isEmptyHandsRequestPending && Time.unscaledTime < _emptyHandsRequestExpires)
                return;

            _isEmptyHandsRequestPending = true;
            _nextEmptyHandsRequestTime = Time.unscaledTime + EmptyHandsRetryDelay;
            _emptyHandsRequestExpires = Time.unscaledTime + EmptyHandsRequestTimeout;
            _localPlayer.SetEmptyHands(result =>
            {
                _isEmptyHandsRequestPending = false;
                if (!string.IsNullOrEmpty(result.Error))
                    BodyDragLog.Warning($"[CorpseDrag] Empty-hands transition failed: {result.Error}");
            });
        }

        private void RestoreLocalPlayerHands()
        {
            _shouldEnforceEmptyHands = false;
            if (!_shouldRestoreHands || _localPlayer == null || _previousHandsItem == null)
                return;
            _shouldRestoreHands = false;
            if (!_localPlayer.IsItemCanBeEquipped(_previousHandsItem))
                return;
            _localPlayer.TryProceed(_previousHandsItem, result =>
            {
                if (!string.IsNullOrEmpty(result.Error))
                    BodyDragLog.Warning($"[CorpseDrag] Previous-item equip failed: {result.Error}");
            });
        }

        private void ActivateRagdoll()
        {
            _originalPutToSleep = _ragdoll._putToSleep;
            bool hasMissingBody = _ragdoll._rigidbodySpawners.Any(spawner => spawner == null || spawner.Rigidbody == null);
            bool wasPhysicsDone = _ragdoll._isPhysicsDone;
            if (!_ragdoll._isPhysicsDone && !hasMissingBody)
            {
                _ragdoll.WakeUp();
                CorpseRagdollSettlement.TakeOver(_ragdoll);
                DragDiagnostics.LogActivation(_ragdoll, wasPhysicsDone, hasMissingBody, _originalPutToSleep, "WakeUp");
                return;
            }

            _ragdoll._putToSleep = false;
            try
            {
                _ragdoll.Start();
            }
            finally
            {
                _ragdoll._putToSleep = _originalPutToSleep;
            }
            CorpseRagdollSettlement.TakeOver(_ragdoll);
            DragDiagnostics.LogActivation(_ragdoll, wasPhysicsDone, hasMissingBody, _originalPutToSleep, "Start");
        }

        // Every ragdoll RigidbodySpawner carries a BodyPartCollider (EFT's own
        // PlayerPoolObject.CreatePlayerRigidbodySleepHierarchy relies on it); RibcageUp is the
        // upper-chest body EFT itself treats as the ragdoll's upper root.
        private bool FindChestBody(out Rigidbody grabbedBody, out Vector3 worldPoint)
        {
            foreach (RigidbodySpawner spawner in _ragdoll._rigidbodySpawners)
            {
                if (spawner == null || spawner.Rigidbody == null)
                    continue;
                if (!spawner.TryGetComponent(out BodyPartCollider part) ||
                    part.BodyPartColliderType != EBodyPartColliderType.RibcageUp)
                    continue;
                grabbedBody = spawner.Rigidbody;
                worldPoint = grabbedBody.worldCenterOfMass;
                return true;
            }

            grabbedBody = null;
            worldPoint = default;
            return false;
        }

        // Ported technique from KeepMeAlive's BodyRagdoll: pull the grabbed body with a kinematic
        // "hand" joined to it by a ConfigurableJoint instead of an explicit spring force. PhysX
        // solves the joint's small linear limit as a near-hard constraint every substep, so there
        // is no spring overshoot/oscillation to tune - the hand just moves at a capped speed and
        // the joint keeps the chest within its slack.
        private void CreateHand(Vector3 worldPoint)
        {
            _handObject = new GameObject("BodyDrag Hand");
            _handObject.transform.position = worldPoint;

            _handBody = _handObject.AddComponent<Rigidbody>();
            _handBody.isKinematic = true;
            _handBody.useGravity = false;

            _handJoint = _handObject.AddComponent<ConfigurableJoint>();
            _handJoint.autoConfigureConnectedAnchor = false;
            _handJoint.anchor = Vector3.zero;
            // hand sits on the chest's center of mass (the grab point), so tether that, not the
            // bone pivot - otherwise the joint starts already stretched and yanks on first tick
            _handJoint.connectedAnchor = _grabbedBody.centerOfMass;
            _handJoint.connectedBody = _grabbedBody;
            _handJoint.xMotion = ConfigurableJointMotion.Limited;
            _handJoint.yMotion = ConfigurableJointMotion.Limited;
            _handJoint.zMotion = ConfigurableJointMotion.Limited;
            _handJoint.linearLimit = new SoftJointLimit { limit = Plugin.HoldSlack.Value };
            _handJoint.angularXMotion = ConfigurableJointMotion.Free;
            _handJoint.angularYMotion = ConfigurableJointMotion.Free;
            _handJoint.angularZMotion = ConfigurableJointMotion.Free;
            _handJoint.projectionMode = JointProjectionMode.PositionAndRotation;
            _handJoint.projectionDistance = 0.1f;
            _handJoint.enablePreprocessing = false;

            _lastHandTarget = worldPoint;
        }

        private void DestroyHand()
        {
            if (_handObject != null)
            {
                // Deactivated first: Destroy only takes effect at the end of the frame, and the
                // joint would keep pulling the chest through any physics step before then.
                _handObject.SetActive(false);
                Destroy(_handObject);
            }
            _handObject = null;
            _handBody = null;
            _handJoint = null;
        }

        // Moves every dragged body (and the hand) by the same offset and stops it, keeping the
        // ragdoll's shape - used when the hold point jumps too far in one tick (teleport) or when
        // the chest has ended up stuck far from where it should be (wedged on geometry).
        private void ShiftDragBodies(Vector3 offset)
        {
            foreach (BodyState state in _bodyStates)
            {
                if (state.Body == null)
                    continue;
                state.Body.position += offset;
                state.Body.velocity = Vector3.zero;
                state.Body.angularVelocity = Vector3.zero;
            }
            if (_handBody != null)
                _handBody.position += offset;
        }

        // Driven from Update, not FixedUpdate: EFT runs Physics.Simulate once per frame from its own
        // Update (Script simulation mode), so Unity's fixed step would give 0 or N hand moves/limb
        // forces per actual physics step, and repeat corrections against not-yet-synced transforms
        private void TickDrag(float deltaTime)
        {
            if (_grabbedBody == null || _camera == null || _corpse == null)
            {
                StopDragging();
                return;
            }

            Transform cameraTransform = _camera.transform;
            Vector3 cameraPosition = cameraTransform.position;
            // rigidbody-derived, not transform: transforms only sync after EFT's simulate (auto
            // sync off), so they lag position writes like ShiftDragBodies
            Vector3 worldPoint = _grabbedBody.worldCenterOfMass;
            _grabDistanceBlendElapsed += deltaTime;
            float distanceBlend = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(_grabDistanceBlendElapsed / HeldDistanceBlendDuration));
            _grabDistance = Mathf.Lerp(_initialGrabDistance, _targetGrabDistance, distanceBlend);
            Vector3 horizontalForward = Quaternion.Euler(0f, cameraTransform.eulerAngles.y, 0f) * Vector3.forward;
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

            // A hold jumping further than this in one tick is a teleport (camera/player snap,
            // vault, etc.), not a pull - move the whole ragdoll with it instead of letting the
            // joint fling it across the gap. Failing that, if the chest still ends up far from
            // the hold (stuck on geometry despite the snag-fix in RepairExcessiveSeparation),
            // relocate it there directly rather than leaving the drag permanently jammed.
            Vector3 jump = target - _lastHandTarget;
            float teleportDistance = Plugin.TeleportDistance.Value;
            if (jump.sqrMagnitude > teleportDistance * teleportDistance)
            {
                ShiftDragBodies(jump);
                worldPoint = _grabbedBody.worldCenterOfMass;
            }
            float maxHoldError = Plugin.MaxHoldError.Value;
            if ((worldPoint - target).sqrMagnitude > maxHoldError * maxHoldError)
                ShiftDragBodies(target - worldPoint);
            _lastHandTarget = target;

            // joint-connected bodies share the grabbed body's physics island, so PhysX wakes them
            // together - only the grabbed body itself needs checking, not the whole ragdoll
            if (_grabbedBody.IsSleeping())
                _grabbedBody.WakeUp();
            _handBody.MovePosition(Vector3.MoveTowards(_handBody.position, target, Plugin.MaxHandSpeed.Value * deltaTime));
            DriveLimbs();
            _jointStability.RepairExcessiveSeparation(deltaTime);

            _diagElapsed += deltaTime;
            if (DragDiagnostics.ShouldProbe(_diagElapsed, ref _diagNextProbe))
                LogDiagState($"tick t={_diagElapsed:F1}");

            SendPoseIfDue();
        }

        // TEMP diagnostics - remove with DragDiagnostics
        private float _diagElapsed;
        private float _diagNextProbe;

        private void LogDiagState(string label)
        {
            List<Rigidbody> bodies = new(_bodyStates.Count);
            foreach (BodyState state in _bodyStates)
                bodies.Add(state.Body);
            DragDiagnostics.LogState(label, _ragdoll, bodies, _grabbedBody, _grabbedBodyPositionAtCapture, _handBody, _jointStability);
        }

        // every other ragdoll body otherwise only moves by being dragged through CharacterJoint
        // constraints from the grabbed body - give each one its own scaled-down spring toward a
        // translation-only carried-along target so limbs keep pace with the torso instead of
        // lagging through finite joint-solver iterations. Translation-only (not a rotated rigid
        // offset) so a turning torso doesn't whip distant limbs through a rotated target - joints
        // still own rotation/swing entirely
        private void DriveLimbs()
        {
            Vector3 grabbedBodyDelta = _grabbedBody.position - _grabbedBodyPositionAtCapture;
            float limbSpring = Plugin.GrabSpring.Value * Plugin.LimbFollowStrength.Value;
            float limbDamping = 2.2f * Mathf.Sqrt(Mathf.Max(limbSpring, 0.01f));

            foreach (BodyState state in _bodyStates)
            {
                if (state.Body == null || state.Body == _grabbedBody)
                    continue;

                Vector3 limbTarget = state.InitialWorldPosition + grabbedBodyDelta;
                Vector3 toTarget = limbTarget - state.Body.position;
                Vector3 limbAccel = toTarget * limbSpring - state.Body.velocity * limbDamping;
                limbAccel = Vector3.ClampMagnitude(limbAccel, Plugin.MaximumGrabAcceleration.Value);
                if (state.Body.IsSleeping())
                    state.Body.WakeUp();
                state.Body.AddForce(limbAccel, ForceMode.Acceleration);
            }
        }

        private void SendPoseIfDue()
        {
            if (!BodyDragSync.Active || Time.unscaledTime < _nextPoseSendTime)
                return;
            _nextPoseSendTime = Time.unscaledTime + PoseSendInterval;
            BodyDragSync.PoseSent?.Invoke(BuildPose());
        }

        private BodyDragPose BuildPose()
        {
            int boneCount = _bodyStates.Count;
            Vector3[] positions = new Vector3[boneCount];
            Quaternion[] rotations = new Quaternion[boneCount];
            for (int i = 0; i < boneCount; i++)
            {
                Rigidbody body = _bodyStates[i].Body;
                positions[i] = body != null ? body.position : Vector3.zero;
                rotations[i] = body != null ? body.rotation : Quaternion.identity;
            }
            return new BodyDragPose
            {
                ProfileId = _corpse.PlayerProfileID,
                Sequence = _poseSequence++,
                Pelvis = _grabbedBody != null ? _grabbedBody.position : Vector3.zero,
                BonePositions = positions,
                BoneRotations = rotations
            };
        }

        private void Update()
        {
            if (_localPlayer?.MovementContext != null)
                _localPlayer.MovementContext.EnableSprint(false);
            RequestEmptyHands();
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
                RestoreLocalPlayerHands();
                if (_active == this) _active = null;
                return;
            }
            if (!_nativeOwnershipStarted)
            {
                if (_active == this) _active = null;
                return;
            }
            if (_ragdoll != null && _grabbedBody != null)
                LogDiagState("release");
            DestroyHand();
            _jointStability.RepairExcessiveSeparation(Time.deltaTime);
            _jointStability.Restore();
            foreach (BodyState state in _bodyStates)
                if (state.Body != null)
                {
                    state.Body.constraints = state.Constraints;
                    state.Body.collisionDetectionMode = state.OriginalCollisionMode;
                    state.Body.velocity = Vector3.ClampMagnitude(state.Body.velocity, 2f);
                    state.Body.angularVelocity = Vector3.ClampMagnitude(state.Body.angularVelocity, 4f);
                }
            if (_localPlayer != null)
                _localPlayer.UpdateSpeedLimitByHealth();
            RestoreLocalPlayerHands();
            _detachedWeapon?.RestoreCollisions();
            if (_corpse != null)
            {
                CorpseRagdollSettlement.Schedule(_corpse, _ragdoll);
                if (BodyDragSync.Active && !_claimDenied && _grabbedBody != null)
                    BodyDragSync.DragStoppedLocally?.Invoke(_corpse.PlayerProfileID, BuildPose());
            }
            if (_active == this)
                _active = null;
            BodyDragLog.Info("[CorpseDrag] Released corpse to EFT settling cycle");
        }
    }
}
