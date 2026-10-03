# Corpse Drag Physics Audit

Source: TraumaCore `CorpseDragController`/`CorpseWeaponLink`/`CorpseRagdollSettlement`/`RagdollJointStability` (ported). Ragdoll-physics focus only.

## Capture (grab start)

1. Snapshot every `RigidbodySpawner` pos/rot before touching anything (dict).
2. `CorpseWeaponLink.Detach` — kill `_weaponJoint` (`DestroyImmediate`), `Physics.IgnoreCollision(true)` every weapon-collider × body-collider pair, cache pairs for restore.
3. `ActivateRagdoll` — ragdoll not yet simulating (`!_isPhysicsDone`, no missing bodies): `WakeUp()`. Else: force `_putToSleep=false`, re-run `Start()`, restore `_putToSleep` after (reboots ragdoll EFT already finalized).
4. `RagdollJointStability.Capture` — per `CharacterJoint`: save projection/preprocessing/break settings, set `breakForce`/`breakTorque = Infinity`, bump `solverIterations≥12`, `solverVelocityIterations≥4`. Flag forearm joints (name match) for elbow clamp later.
5. Re-apply pre-capture snapshot pose to every body (position/rotation/zero velocity), then: `constraints = None`, `isKinematic = false`, `WakeUp()`. One `Physics.SyncTransforms()` after loop.
6. Pick grab body: nearest rigidbody to chest anchor transform by distance; grab point = `worldCenterOfMass`, stored as local-space offset on that body.

Net effect: ragdoll goes "EFT settled/kinematic-ish" → "fully dynamic, unbreakable joints, high solver iterations" in one frame. Weapon decoupled.

## Per-tick drive (FixedUpdate)

- Target point = camera position + yaw-only forward × blended distance, height pinned to capture-time height + camera height delta. No pitch influence — body doesn't fly up/down on look-up.
- Distance blends initial→target via `SmoothStep` over `HeldDistanceBlendDuration`.
- Force = critically-damped spring: `(target - worldPoint) * GrabSpring - GetPointVelocity(worldPoint) * GrabDamping`, clamped to `MaximumGrabAcceleration`, applied as `ForceMode.Acceleration` at grab point only. Only grabbed body gets force — rest of ragdoll follows through joints, not independent springs.
- Every captured body force-`WakeUp()`'d each tick — stops engine auto-sleep from freezing a slow-moving limb.
- `RagdollJointStability.RepairExcessiveSeparation` runs every tick: elbow joints, clamp fold angle to 135° by rotating forearm rigidbody directly (position+rotation correction, velocity zeroed); all joints, anchor separation > 0.025m → pull body back along separation vector to within 0.015m, velocity zeroed. Direct position correction outside the solver — compensates for joints staying unbreakable under spring load.
- Separation watchdog: grab point too far from camera for `MaximumSeparationSeconds` → auto-release.

## Release (OnDestroy)

1. `RepairExcessiveSeparation` once more, then `RagdollJointStability.Restore` — joint break force/torque/projection/solver iterations back to captured values. Joints breakable again.
2. Per body: restore original `constraints`, clamp velocity ≤2 m/s, angular velocity ≤4 m/s — prevents release-flung limbs.
3. Weapon collisions restored (`IgnoreCollision(false)`).
4. `CorpseRagdollSettlement.Schedule` — `_putToSleep=true`, then coroutine: wait 1s (handoff delay), poll every frame (≤8s) while any body velocity/angular velocity exceeds `0.08 m/s`. On settle: all rigidbodies still present → `ragdoll.ForceStopRigidBody()` (EFT's own bulk-stop); any body missing → manual zero velocity + `isKinematic=true` + `Sleep()` per surviving body instead (bulk-stop throws `NullReferenceException` on partial ragdolls).

## Key physics properties overridden, and why

| Property | Normal EFT value | During drag | Why |
|---|---|---|---|
| Joint `breakForce`/`breakTorque` | finite | `Infinity` | spring force would otherwise snap joints at high grab distance/speed |
| `solverIterations`/`solverVelocityIterations` | engine default | ≥12 / ≥4 | more iterations needed to keep unbreakable, force-driven joint chain stable |
| Body `constraints` | varies | `None` | full 6-DoF freedom needed to pull body anywhere |
| Body `isKinematic` | often `true` post-settle | `false` | must be dynamic to receive `AddForceAtPosition` |
| `_putToSleep` | `true` once settled | `false` during drag, `true` again after handoff | prevents engine freezing body mid-drag; re-enabled to re-settle after |

## Known fight points (not resolved here)

- Spring force vs. unbreakable joints vs. terrain collision = three forces fighting same bodies every tick. `RepairExcessiveSeparation`'s direct position correction is a patch over this, not a fix.
- No ground-clearance awareness. Spring happily pushes grab body into terrain; only joint-separation repair pulls limbs back, nothing pushes body out of geometry.
- `ForceStopRigidBody` assumes complete, still-attached rigidbody set. Any body lost mid-raid (despawn, detach) forces slower per-body manual stop path.
