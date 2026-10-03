# BodyDragging Devlog

Client-side SPT 4.1 (Tushonka) BepInEx mod: drag corpses. Optional Fika co-op sync. netstandard2.1.
Plan source: `C:\Users\kobethuy\.claude\plans\bodydrag-standalone-fika-sync-plan.md`. Origin: ported from TraumaCore (`D:\Git Repo\traumacore-spt`, Apache-2.0, Hysocs). Later techniques from KeepMeAlive (`D:\Git Repo\KeepMeAlive`, `BodyRagdoll.cs`).

## Status (2026-10-03)

| Plan step | State |
|---|---|
| 1. Solo mod (TraumaCore port) | Done |
| 2. `BodyDragSync` seam + reflection loader | Done |
| 3. `BodyDragFika` bridge | Done |
| 4. Test matrix (solo/host/client/headless) | Not run |

- **Never build-verified by agent** — no SPT install in sandbox. User builds/tests locally. `SptRoot` = `F:\SPT_4.1` (user-set).
- **Open bug:** first drag on fresh corpse (immediately after kill, every corpse) — ragdoll not active; stop → body ragdolls; 2nd drag fine. Fix #2 applied, untested. See "Open bug" below.
- **Temp diagnostics live** (`DragDiagnostics.cs`) — remove once bug closed.

## Layout

```
SPT-BodyDragging.slnx
LICENSE (Apache-2.0)  NOTICE (credits Hysocs, lists modifications)  AUDIT.md  DEVLOG.md
SPT-BodyDragging/            main plugin (AssemblyName BodyDragging)
  Plugin.cs                  [BepInPlugin com.kobethuy.bodydragging], config, PatchManager(autoPatch), Fika bridge reflection loader
  AssemblyInfo.cs            [InternalsVisibleTo("BodyDragFika")]
  BodyDragSync.cs            seam: flags Active/IsHost/HeadlessHost, Tick, events (DragStartRequested/PoseSent/DragStoppedLocally), apply delegates, IsProfileAlreadyDragged; BodyDragPose struct
  BodyDragLog.cs             Info gated by DebugLogging
  Features/
    CorpseDragController.cs  core drag MonoBehaviour on local player
    RagdollJointStability.cs unbreakable joints, solver iters, anchor repair, elbow clamp, snag-fix
    CorpseRagdollSettlement.cs post-release settle + EFT death-cycle takeover/handback
    CorpseWeaponLink.cs      detach weapon joint, ignore weapon↔body collisions
    RemoteCorpseDragFollower.cs peer side: applies received poses via Corpse.ApplyTransformSync
    DragDiagnostics.cs       TEMP probe
  Patches/
    AddCorpseDragActionPatch.cs   InteractionContextHelper.GetAvailableActions(GamePlayerOwner, LootItem) postfix: DRAG BODY / STOP DRAGGING
    CorpseDragMovementPatch.cs    MovementContext.ClampSpeed postfix: speed × DragSpeedMultiplier
BodyDragFika/                sideloaded bridge, NOT a BepInEx plugin (no Fika ref in main asm → solo no load error)
  BodyDragFika.cs            BodyDragFikaBridge.Initialize/Shutdown; packets DragStart/DragDeny/DragPose/DragStop
```
Deleted: `Class1.cs`, `ChestAnchorResolver.cs`, `LockAimToCorpsePatch.cs`, `CorpseRagdollWorkingCyclePatch.cs`.

## Architecture

**Local drag (`CorpseDragController`)**
- `Begin` → `Capture`: snapshot spawner poses → `CorpseWeaponLink.Detach` → `ActivateRagdoll` → `RagdollJointStability.Capture` → per body: restore pose, zero vel, constraints None, kinematic false, `CollisionDetectionMode.Continuous`, WakeUp → `FindChestBody` → record initial positions → `CreateHand`.
- `ActivateRagdoll`: not done & no missing body → `WakeUp()` then `TakeOver`; else `_putToSleep=false`, `Start()`, restore, `TakeOver`.
- `FindChestBody`: spawner whose `BodyPartCollider.BodyPartColliderType == RibcageUp` (EFT's own `PlayerPoolObject` relies on every spawner having one).
- Grab = chest only, always. No aim-dependent anchor (confirmed; user misread earlier).
- Drive = **kinematic tether** (from KeepMeAlive): `"BodyDrag Hand"` kinematic Rigidbody + `ConfigurableJoint` to chest, linear limit `HoldSlack`, angular free, projection PositionAndRotation 0.1, preprocessing off. Hand moved `MovePosition(MoveTowards(..., MaxHandSpeed))`. Spring force removed.
- Target = camera pos + yaw-only forward × blended distance (0.25s SmoothStep initial→target), height = grab height + camera height delta.
- Recovery: hold jump > `TeleportDistance` → `ShiftDragBodies(jump)`, `worldPoint += jump` (transform stale: EFT auto-sync off); chest > `MaxHoldError` from target → shift to target. Auto-release after `MaximumSeparationSeconds` outside interaction range.
- `DriveLimbs`: per non-chest body, spring toward `InitialWorldPosition + (chest.pos - chestPosAtCapture)` (translation only), spring = `GrabSpring × LimbFollowStrength`, damping `2.2·√spring`, clamp `MaximumGrabAcceleration`, `AddForce` Acceleration.
- `RepairExcessiveSeparation` each tick: elbow fold clamp 135°; anchor gap > 0.08m = **snag** → full gap close, zero vel, body's own solid colliders `isTrigger=true` 0.35s; gap > 0.025 → partial pull to 0.015.
- Only grabbed body WakeUp-checked (joint island wakes rest).
- `Update`: disable sprint, keep hands empty. `OnDestroy`: destroy hand first (SetActive false then Destroy), repair, restore joints/constraints/collision mode, clamp vel ≤2 / angVel ≤4, restore hands, restore weapon collisions, `Settlement.Schedule`, send DragStopped (unless claim denied).

**EFT internals (verified from `D:\Git Repo\Assembly-CSharp`)**
- `CorpseRagdoll.Start()`: spawner `Create()` joints+rigidbodies (rebuild from prefab settings if `Remove()`d), kinematic false, `PhysicsExtensions.UpdateController.SupportRigidbody`, `_isPhysicsDone=false`, starts `WorkingCycle` if `_putToSleep`.
- `WorkingCycle`: 1s wait, loop: `TryPutToSleep` on `_rigidbodySleepHierarchy` (sticky `MustBeSleeping`), exit when `_checkCorpseIsStill(allSleeping, t)` = `sleeping || t ≥ 15s` → `StopRigidbody` all (kinematic, Discrete, **unsupported**), `_isPhysicsDone=true`, `OnRigidbodyStopped` (MakeVisible + RegisterInCullingObject) → wait while `_isVisibleTest()` → `GetPartsTogether` → `Remove()` joints/rigidbodies (`_keepRigidbody` false for real corpses).
- EFT physics: `Physics.simulationMode=Script`, `Physics.Simulate` in Update (SmoothSimulate), only while ≥1 supported active rigidbody. `autoSyncTransforms` off.
- `CreateStillCorpse` (raid-load corpses) → `HasRagdoll=false` → `Ragdoll` null → not draggable.

**Fika (`BodyDragFika`)**
- Loaded by `Plugin.TryLoadFikaBridge` only if `Chainloader.PluginInfos` has `com.fika.core` and `BodyDragFika.dll` beside main DLL.
- Events: `FikaGameCreatedEvent` (Active, IsHost=`FikaBackendUtils.IsServer`, HeadlessHost=`IsHeadlessGame||IsHeadless`), `FikaGameEndedEvent`, `PeerDisconnectedEvent` (host frees claims; `e.Peer` name **unverified**). Packet registration retried via `BodyDragSync.Tick` (Blackout pattern).
- Host = claim authority, first claim wins (`Claims: profileId→NetPeer`, null = host). Client→host→relay `SendData(..., peerToIgnore)`. Deny broadcast (no unicast API), only requester reacts.
- Pose: per-bone pos+rot, variable count, `Sequenced`, ~15Hz (~6KB/s/drag). Stop: ReliableOrdered with final pose. Headless: `HeadlessApplyFinalPoseOnly`.
- Peer `RemoteCorpseDragFollower`: find corpse by `PlayerProfileID` (ObservedPlayersCorpses values, fallback `FindObjectsOfType` w/ 0.5s miss cooldown), `TakeOver`, detach weapon, `ApplyTransformSync`, 1.5s stale timeout → release.

## Config (`Plugin.cs` defaults)

| Key | Default | Note |
|---|---|---|
| Enable Body Dragging | true | |
| Max Hand Speed | 4.5 m/s | tether pace |
| Hold Slack | 0.05 m | tether limit |
| Teleport Distance | 2.5 m | |
| Max Hold Error | 2 m | |
| Grab Spring | 8000 | **user-set**; now limb-assist baseline only → likely too hot, retune |
| Max Grab Acceleration | 8000 | **user-set**; limb-assist clamp |
| Held Distance Multiplier | 0.5 | **user-set** |
| Max Separation Seconds | 5 | |
| Limb Follow Strength | 0.5 | **user-set** |
| Drag Speed Multiplier | 0.2 | |
| Unequip Hands While Dragging | true | via `SetEmptyHands`/`TryProceed` (Fika-safe) |
| Allow Zombie/Bot Corpses | true | |
| Debug Logging | false | |
| Headless Applies Final Pose Only | true | |
Removed: Grab Damping, Lock Aim While Dragging.

## Changelog (chronological)

1. Scaffold + port TraumaCore rip list, Fika bridge, LICENSE/NOTICE.
2. `InternalsVisibleTo("BodyDragFika")` — bridge used internal members (compile fix).
3. Perf: follower `FindObjectsOfType` miss-cooldown.
4. Feedback: speed 0.5→0.2; spring/damping/accel retuned; blend 0.75→0.25s. Terrain-stuck: insights only.
5. AUDIT.md (physics audit of TraumaCore drag — now partly outdated: describes spring drive).
6. Aim lock (`MovementContext.Rotation` setter prefix, freelook exempt) — added then **reverted** per user.
7. Perf: WakeUp loop → only grabbed body.
8. `DriveLimbs` limb-assist spring (planned via plan mode). Bug: skip-guard compared drift vs 0.025m joint const → assist never fired on distal limbs → guard removed.
9. KeepMeAlive comparison (their files missing from repo until user pushed). Findings: tether not spring, snag-fix, continuous collision, teleport recovery, separate contact colliders, network = sync dragger id + local sim per peer.
10. Ported tether, continuous collision, snag-fix (simplified: own colliders, fixed timer), teleport/stuck recovery. Grab Damping removed.
11. Perf pass (main project only): cached `state.Body` in repair; ClampSpeed patch static check first; FixedUpdate transform caching + squared compares; RibcageUp lookup (deleted ChestAnchorResolver); snag colliders = own solid only. Skipped Fika-path items (pose delta-suppression, buffer reuse).
12. First-drag bug fix #1: Harmony postfix wrapping `WorkingCycle` → **failed** (likely Mono inlines tiny iterator stub into `Start()`, detour bypassed).
13. Fix #2: field swap takeover (below). Diagnostics added.

## Bug: first drag on fresh corpse — root cause found (fix #3, untested)

- Symptom: every corpse, drag immediately after kill → not ragdolling; stop → ragdolls; 2nd drag works.
- **Evidence (`Player.log` `[DragDiag]`):** 1st drag `WakeUp` path: `supported=0 supportedTotal=0 canSimulate=False`, chestVel 0. 2nd drag `Start` path: `supported=12 canSimulate=True`, moves.
- **Root cause:** EFT only calls `Physics.Simulate` while ≥1 rigidbody registered via `UpdateController.SupportRigidbody`. Fresh corpse bodies unregistered → no physics steps. `CorpseRagdoll.Start()` registers → 2nd drag worked.
- Fixes #1 (Harmony wrap of `WorkingCycle`) / #2 (field-swap takeover) targeted wrong cause. #2 kept — still valid protection vs death cycle sleeping/stopping/removing bodies mid-drag.
- **Fix #3:** `EnsurePhysicsStepped` registers each dragged body (deduped — Support doesn't dedupe, Unsupport removes one). `StopAvailableBodies` now unsupports (`ForceStopRigidBody` already does).
- **Secondary (also in log):** `chestMoved=27.06` runaway — `MaxHoldError` read chest via stale transform, shift wrote rigidbody → shifted every tick. Fixed: grab point = `_grabbedBody.worldCenterOfMass` (`_localGrabPoint` removed); hand joint `connectedAnchor = centerOfMass` (was pivot → 0.12m initial stretch); drag tick moved `FixedUpdate` → `Update` (`TickDrag(Time.deltaTime)`) to match EFT's once-per-frame simulate.
- Next: user retests. Then remove diagnostics.
- Probe logs: activation path, wasPhysicsDone, missing bodies, putToSleep; capture/every 0.5s for 4s/release: kinematic/sleeping/supported counts, chest kin/sleep/vel/moved, hand gap, snags/released, CanRunSimulate, sim mode, autoSync.
- Remove after: `DragDiagnostics.cs`, `LogDiagState`/`_diag*` + call sites in `CorpseDragController`, `SnagEvents`/`JointCount`/`ReleasedCount` in `RagdollJointStability`.

## Known gaps / backlog

- Not build-verified; `PeerDisconnectedEvent.Peer` unverified.
- Snag release: fixed 0.35s timer, no overlap check before re-enable (KeepMeAlive uses `ComputePenetration`).
- Extremities share `MaximumGrabAcceleration` with limbs — possible whip.
- Fika: no pose interpolation on peers (snap at ~15Hz); no delta-suppression; per-packet array allocs.
- Taken-over corpses: EFT cleanup deferred until handback.
- Plan §5 items: ObservedCorpse finale coroutines vs forced poses, late-joiner resync, loot-open while dragged, mod-missing peers.
- AUDIT.md outdated re: drive mechanism.

## Conventions / user prefs

- Caveman Ultra replies + docs.
- No LINQ in client per-frame paths (memory: `feedback_spt_client_linq.md`); one-shot OK; server mods exempt.
- Don't bandaid; best-of-both, straightforward. Plan first when asked.
- Respect user's on-disk edits (config defaults, csproj SptRoot).
