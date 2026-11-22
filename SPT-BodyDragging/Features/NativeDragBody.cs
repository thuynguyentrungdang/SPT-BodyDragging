using System.Collections.Generic;
using System.Linq;
using EFT;
using EFT.Interactive;
using UnityEngine;
using BodyDragging.Integration;

namespace BodyDragging.Features
{
    // The physics half of the native (non-Rupture) drag, shared by the local dragger
    // (CorpseDragController) and every other peer's simulation of the same body
    // (RemoteCorpseDragSim): take the corpse's own ragdoll over, hold its chest with a kinematic hand
    // tethered by a small joint, assist the limbs, and hand it back to EFT's settle cycle on release.
    // Callers decide WHERE the hand goes (camera on the dragger, the dragger's synced pose elsewhere).
    internal sealed class NativeDragBody
    {
        // Below this a body counts as settled rather than still actively falling/flailing.
        private const float SettleVelocityThreshold = 0.3f;
        private const float SettleAngularVelocityThreshold = 1f;
        // Calm has to hold for a short stretch, not just one lucky frame mid-flail.
        internal const float RequiredCalmSeconds = 0.3f;
        // Safety cap so a corpse that never reads as calm (edge case) doesn't block the grab
        // forever - accept the residual risk after this long rather than brick dragging entirely.
        internal const float MaxSettleWaitSeconds = 3f;

        private sealed class BodyState
        {
            internal Rigidbody Body;
            internal RigidbodyConstraints Constraints;
            internal Vector3 InitialWorldPosition;
            internal CollisionDetectionMode OriginalCollisionMode;
        }

        private readonly Corpse _corpse;
        private readonly CorpseRagdoll _ragdoll;
        private readonly List<BodyState> _bodyStates = new();
        private readonly RagdollJointStability _jointStability = new();
        private readonly NativeRigTuning _rigTuning = new();
        private CorpseWeaponLink.DetachedWeapon _detachedWeapon;
        private Rigidbody _grabbedBody;
        private Vector3 _grabbedBodyPositionAtCapture;
        private GameObject _handObject;
        private Rigidbody _handBody;
        private ConfigurableJoint _handJoint;
        private Vector3 _lastHandTarget;
        private bool _originalPutToSleep;
        private float _diagElapsed;
        private float _diagNextProbe;

        // A body that waits this long for Rupture's Release is given up on.
        private const float AcquireTimeoutSeconds = 5f;

        // Handoff mode (Rupture 1.4.0+): Rupture owns the corpse until HandoffLeases releases it to this peer. Its
        // native bodies then exist but are kinematic and unsupported, so there is no EFT ragdoll cycle to take over
        // (ActivateRagdoll/TakeOver/Schedule are skipped), and the final pose + Adopt happen in HandoffLeases.
        private readonly bool _handoff;
        private Transform[] _severedRoots = System.Array.Empty<Transform>();
        private bool _skipWeaponDetach;
        private float _acquireStartedAt = -1f;

        internal NativeDragBody(Corpse corpse, bool handoff = false)
        {
            _corpse = corpse;
            _ragdoll = corpse.Ragdoll;
            _handoff = handoff;
        }

        internal enum EngageResult { Engaged, Pending, Failed }

        // Settle gate shared by the local drag and the remote simulation. Handoff: Rupture's Simulating flag is already
        // binary and Release quiesces the body, so engage the moment it is not simulating (no calm-time accumulation).
        // Otherwise: calm for RequiredCalmSeconds. Either way MaxSettleWaitSeconds ends the wait.
        internal bool ReadyToEngage(bool calm, float calmElapsed, float settleElapsed) =>
            settleElapsed >= MaxSettleWaitSeconds || (_handoff ? calm : calmElapsed >= RequiredCalmSeconds);

        internal bool UsesHandoff => _handoff;
        internal bool IsEngaged => _grabbedBody != null;
        internal Rigidbody GrabbedBody => _grabbedBody;
        // rigidbody-derived, not transform: transforms only sync after EFT's simulate (auto sync
        // off), so they lag position writes like ShiftBodies
        internal Vector3 ChestCenter => _grabbedBody.worldCenterOfMass;

        // Handoff: before the Release Rupture's native bodies are kinematic proxies that report no velocity, so its own
        // "still simulating" flag is the gate; afterwards the released body starts at rest.
        internal bool IsCalm()
        {
            if (_handoff)
                return !HandoffLeases.IsSimulating(_corpse);
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

        // The real grab: only once the corpse is confirmed calm. Handoff mode first takes the corpse from Rupture
        // (Pending until it reports Released). Failed: the caller releases, which still hands the corpse back.
        internal EngageResult TryEngage()
        {
            if (_handoff)
            {
                float now = Time.unscaledTime;
                if (_acquireStartedAt < 0f)
                    _acquireStartedAt = now;
                switch (HandoffLeases.Acquire(_corpse, out HandoffInfo info))
                {
                    case LeaseAcquire.Refused:
                        return EngageResult.Failed;
                    case LeaseAcquire.Pending:
                        return now - _acquireStartedAt > AcquireTimeoutSeconds ? EngageResult.Failed : EngageResult.Pending;
                }
                _severedRoots = info?.SeveredRoots ?? System.Array.Empty<Transform>();
                // the weapon is its own Rupture body then, not hanging off the hand: leave it alone
                _skipWeaponDetach = info != null && info.WeaponIndependent;
            }
            return Engage() ? EngageResult.Engaged : EngageResult.Failed;
        }

        // spawner bones whose subtree Rupture removed (severed limbs): their bodies are parked, never dragged
        private bool IsSevered(Component spawner)
        {
            foreach (Transform root in _severedRoots)
                if (root != null && spawner.transform.IsChildOf(root))
                    return true;
            return false;
        }

        private bool Included(RigidbodySpawner spawner) => spawner != null && !IsSevered(spawner);

        private bool Engage()
        {
            RigidbodySpawner[] spawners = _ragdoll._rigidbodySpawners.Where(Included).ToArray();
            Dictionary<RigidbodySpawner, (Vector3 Position, Quaternion Rotation)> poses =
                spawners.ToDictionary(spawner => spawner, spawner => (spawner.transform.position, spawner.transform.rotation));
            _detachedWeapon = _skipWeaponDetach ? null : CorpseWeaponLink.Detach(_ragdoll);
            if (!_handoff)
                ActivateRagdoll();
            else
                IdleEftDeathCycle();
            CharacterJointSpawner[] joints = (_ragdoll._jointSpawners ?? System.Array.Empty<CharacterJointSpawner>())
                .Where(joint => joint != null && !IsSevered(joint)).ToArray();
            _jointStability.Capture(joints);
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
            NativeRigTuning.SeatOnJoints(joints);
            _rigTuning.Capture(spawners);
            if (_handoff)
                LogHandoffState(spawners, joints);
            Physics.SyncTransforms();
            if (!FindChestBody(out _grabbedBody, out Vector3 worldPoint))
                return false;

            _grabbedBodyPositionAtCapture = _grabbedBody.position;
            foreach (BodyState state in _bodyStates)
                if (state.Body != null)
                    state.InitialWorldPosition = state.Body.position;
            CreateHand(worldPoint);
            LogDiagState("capture");
            return true;
        }

        // Driven from Update, not FixedUpdate: EFT runs Physics.Simulate once per frame from its own
        // Update (Script simulation mode), so Unity's fixed step would give 0 or N hand moves/limb
        // forces per actual physics step, and repeat corrections against not-yet-synced transforms
        internal void Tick(Vector3 target, float deltaTime)
        {
            Vector3 worldPoint = _grabbedBody.worldCenterOfMass;
            // A hold jumping further than this in one tick is a teleport (camera/player snap,
            // vault, etc.), not a pull - move the whole ragdoll with it instead of letting the
            // joint fling it across the gap. Failing that, if the chest still ends up far from
            // the hold (stuck on geometry despite the snag-fix in RepairExcessiveSeparation),
            // relocate it there directly rather than leaving the drag permanently jammed.
            Vector3 jump = target - _lastHandTarget;
            float teleportDistance = Plugin.TeleportDistance.Value;
            if (jump.sqrMagnitude > teleportDistance * teleportDistance)
            {
                ShiftBodies(jump);
                worldPoint = _grabbedBody.worldCenterOfMass;
            }
            float maxHoldError = Plugin.MaxHoldError.Value;
            if ((worldPoint - target).sqrMagnitude > maxHoldError * maxHoldError)
                ShiftBodies(target - worldPoint);
            _lastHandTarget = target;

            // joint-connected bodies share the grabbed body's physics island, so PhysX wakes them
            // together - only the grabbed body itself needs checking, not the whole ragdoll
            if (_grabbedBody.IsSleeping())
                _grabbedBody.WakeUp();
            _handBody.MovePosition(Vector3.MoveTowards(_handBody.position, target, Plugin.MaxHandSpeed.Value * deltaTime));
            _rigTuning.LimitSpeeds(_grabbedBody);
            DriveLimbs(deltaTime);
            _jointStability.RepairExcessiveSeparation(deltaTime);

            _diagElapsed += deltaTime;
            if (DragDiagnostics.ShouldProbe(_diagElapsed, ref _diagNextProbe))
                LogDiagState($"tick t={_diagElapsed:F1}");
        }

        // Back to EFT: its settle cycle finishes the corpse. Safe whether or not Engage ran.
        internal void Release()
        {
            if (_grabbedBody != null)
                LogDiagState("release");
            DestroyHand();
            _jointStability.RepairExcessiveSeparation(Time.deltaTime);
            _jointStability.Restore();
            _rigTuning.Restore();
            foreach (BodyState state in _bodyStates)
                if (state.Body != null)
                {
                    state.Body.constraints = state.Constraints;
                    state.Body.collisionDetectionMode = state.OriginalCollisionMode;
                    state.Body.velocity = Vector3.ClampMagnitude(state.Body.velocity, 2f);
                    state.Body.angularVelocity = Vector3.ClampMagnitude(state.Body.angularVelocity, 4f);
                }
            _bodyStates.Clear();
            _grabbedBody = null;
            _detachedWeapon?.RestoreCollisions();
            _detachedWeapon = null;
            // handoff: the bodies keep settling under Unity physics until HandoffLeases applies the final pose and adopts
            if (!_handoff)
                CorpseRagdollSettlement.Schedule(_corpse, _ragdoll);
        }

        // Returns a handed-off corpse's bodies to the shape Rupture expects (kinematic, no longer stepped by EFT) just
        // before its Adopt strips and rebuilds them from the final bone poses.
        internal static void FinishHandoffPhysics(CorpseRagdoll ragdoll)
        {
            if (ragdoll?._rigidbodySpawners == null)
                return;
            CorpseRagdollSettlement.HandBack(ragdoll);
            foreach (RigidbodySpawner spawner in ragdoll._rigidbodySpawners)
            {
                Rigidbody body = spawner != null ? spawner.Rigidbody : null;
                if (body == null)
                    continue;
                if (!body.isKinematic)
                {
                    body.velocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
                    body.isKinematic = true;
                }
                // Unsupport removes one registration per call; ours is at most one
                for (int attempt = 0; attempt < 4 && IsStepped(body); attempt++)
                    PhysicsExtensions.UpdateController.UnsupportRigidbody(body);
            }
        }

        private static bool IsStepped(Rigidbody body)
        {
            foreach (PhysicsExtensions.UpdateController.RigidbodyData data in PhysicsExtensions.UpdateController._rigidbodies)
                if (data.rigidbody == body)
                    return true;
            return false;
        }

        // First live handoff: what Rupture actually leaves behind (joints, collision) is documented, not yet observed.
        private void LogHandoffState(RigidbodySpawner[] spawners, CharacterJointSpawner[] joints)
        {
            int missingBodies = 0, noCollision = 0, missingJoints = 0;
            foreach (RigidbodySpawner spawner in spawners)
            {
                Rigidbody body = spawner.Rigidbody;
                if (body == null) { missingBodies++; continue; }
                if (!body.detectCollisions) noCollision++;
            }
            foreach (CharacterJointSpawner spawner in joints)
                if (spawner.GetComponent<CharacterJoint>() == null)
                    missingJoints++;
            BodyDragLog.Info($"[Handoff] Engaged {_corpse.PlayerProfileID}: {spawners.Length} bodies ({missingBodies} missing, {noCollision} without collision), " +
                $"{joints.Length} joints ({missingJoints} missing), severed roots {_severedRoots.Length}, weapon left to Rupture {_skipWeaponDetach}");
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

        // Handoff skips ActivateRagdoll, but EFT's death coroutine (CorpseRagdoll.WorkingCycle) can still be alive on a
        // fresh corpse: an ObservedCorpse (every corpse for a client on a headless host) only counts as still after ~30 s
        // or a CorpseSyncPacket.Done that Fika no longer sends. Alive, it Sleep()s our bodies every frame (client log:
        // physicsDone=False, sleeping=12, chestVel 0). TakeOver idles it; FinishHandoffPhysics hands it back.
        private void IdleEftDeathCycle()
        {
            if (_ragdoll._isPhysicsDone)
                return;
            _ragdoll.WakeUp();
            CorpseRagdollSettlement.TakeOver(_ragdoll);
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
                Object.Destroy(_handObject);
            }
            _handObject = null;
            _handBody = null;
            _handJoint = null;
        }

        // Moves every dragged body (and the hand) by the same offset and stops it, keeping the
        // ragdoll's shape - used when the hold point jumps too far in one tick (teleport) or when
        // the chest has ended up stuck far from where it should be (wedged on geometry).
        private void ShiftBodies(Vector3 offset)
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

        // every other ragdoll body otherwise only moves by being dragged through CharacterJoint
        // constraints from the grabbed body - give each one its own scaled-down spring toward a
        // translation-only carried-along target so limbs keep pace with the torso instead of
        // lagging through finite joint-solver iterations. Translation-only (not a rotated rigid
        // offset) so a turning torso doesn't whip distant limbs through a rotated target - joints
        // still own rotation/swing entirely
        private void DriveLimbs(float deltaTime)
        {
            Vector3 grabbedBodyDelta = _grabbedBody.position - _grabbedBodyPositionAtCapture;
            float limbSpring = Plugin.GrabSpring.Value * Plugin.LimbFollowStrength.Value;
            float step = Mathf.Clamp(deltaTime, LimbAssistMath.MinimumStep, LimbAssistMath.MaximumStep);

            foreach (BodyState state in _bodyStates)
            {
                if (state.Body == null || state.Body == _grabbedBody)
                    continue;

                Vector3 limbTarget = state.InitialWorldPosition + grabbedBodyDelta;
                Vector3 toTarget = limbTarget - state.Body.position;
                Vector3 limbAccel = LimbAssistMath.Acceleration(toTarget, state.Body.velocity, limbSpring, step,
                    Plugin.MaximumGrabAcceleration.Value);
                if (state.Body.IsSleeping())
                    state.Body.WakeUp();
                state.Body.AddForce(limbAccel, ForceMode.Acceleration);
            }
        }

        // TEMP diagnostics - remove with DragDiagnostics
        private void LogDiagState(string label)
        {
            List<Rigidbody> bodies = new(_bodyStates.Count);
            foreach (BodyState state in _bodyStates)
                bodies.Add(state.Body);
            DragDiagnostics.LogState(label, _ragdoll, bodies, _grabbedBody, _grabbedBodyPositionAtCapture, _handBody, _jointStability);
        }
    }
}
