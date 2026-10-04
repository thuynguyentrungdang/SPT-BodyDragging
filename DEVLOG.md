# BodyDragging Devlog

Client-side SPT 4.1 (Tushonka) BepInEx mod: drag corpses. Optional Fika co-op sync. netstandard2.1.
Plan source: `C:\Users\kobethuy\.claude\plans\bodydrag-standalone-fika-sync-plan.md`. Origin: ported from TraumaCore (`D:\Git Repo\traumacore-spt`, Apache-2.0, Hysocs). Drag-mechanic research: KeepMeAlive (`D:\Git Repo\KeepMeAlive`).

## Status (2026-10-04)

| Plan step | State |
|---|---|
| 1. Solo mod (TraumaCore port) | Done |
| 2. `BodyDragSync` seam + reflection loader | Done |
| 3. `BodyDragFika` bridge | Done |
| 4. Test matrix (solo/host/client/headless) | Solo + headless confirmed working; host/client-non-headless not yet explicitly retested |

- **Never build-verified by agent** — no SPT install in sandbox. User builds/tests locally. `SptRoot` = `F:\SPT_4.1` (user-set).
- Drive mechanism: **real dynamic ragdoll (joint-tether), gated behind a settle-and-wait step** (see Architecture). A fully kinematic rigid-carry was tried in between (zero explosion risk, but looked like dragging a frozen statue - no organic limb motion) and was explicitly rejected: user wants actual ragdoll behavior during the drag, not a rigid carry. Current design is the joint-tether drive restored, with a wait-for-calm gate in front of it instead of grabbing immediately.
- **Confirmed working** (solo and headless) once a conflicting third-party mod (`ObservedCorpseSleepPatch`, not ours) was disabled - see changelog #18-19. That mod froze corpses kinematic outside EFT's own settle path, defeating our calm-check. No fix needed on our side for that specific mod; `IsRagdollCalm`'s kinematic-skip is still fragile against other mods doing the same thing (backlog).
- Temp diagnostics live (`DragDiagnostics.cs`) — remove once confirmed clean.

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
    CorpseDragController.cs  core drag MonoBehaviour on local player - settle-wait then joint-tether drag
    RagdollJointStability.cs unbreakable joints, solver iters, anchor repair (deltaTime-capped), elbow clamp, snag-fix
    CorpseRagdollSettlement.cs post-release settle + EFT death-cycle takeover/handback
    CorpseWeaponLink.cs      detach weapon joint, ignore weapon↔body collisions
    RemoteCorpseDragFollower.cs peer side: applies received poses via Corpse.ApplyTransformSync
    DragDiagnostics.cs       TEMP probe
  Patches/
    AddCorpseDragActionPatch.cs   InteractionContextHelper.GetAvailableActions(GamePlayerOwner, LootItem) postfix: DRAG BODY / STOP DRAGGING
    CorpseDragMovementPatch.cs    MovementContext.ClampSpeed postfix: speed × DragSpeedMultiplier
    ObservedCorpseNetSyncPatch.cs ObservedCorpse.MoveTo prefix: no-op EFT's native corpse net-sync while we're dragging that corpse
    ObservedCorpseStillnessPatch.cs ObservedCorpse.CheckCorpseIsStill postfix: force not-still while we've claimed the corpse (blocks any mod that force-freezes via this path)
BodyDragFika/                sideloaded bridge, NOT a BepInEx plugin (no Fika ref in main asm → solo no load error)
  BodyDragFika.cs            BodyDragFikaBridge.Initialize/Shutdown; packets DragStart/DragDeny/DragPose/DragStop
```
Deleted: `Class1.cs`, `ChestAnchorResolver.cs`, `LockAimToCorpsePatch.cs`, `CorpseRagdollWorkingCyclePatch.cs`.

## Architecture

**Local drag (`CorpseDragController`) — settle-and-wait, then real dynamic joint-tether**
- `Begin`: denies the grab if another peer already claims the corpse, else claims it immediately (`ClearInteractionState`, `DragStartRequested`) and starts `BeginSettling` - physics isn't touched yet. (A `BodyDragSync.HeadlessHost` guard was tried here and reverted - that flag is `IsHeadlessGame||IsHeadless`, and `IsHeadlessGame` is true for *every* client connected to a headless-hosted raid, not just the headless process itself; the guard blocked real human clients from ever dragging on headless raids. The actual "no human player" case - the headless process itself - is already covered a few lines later by `GamePlayerOwner.MyPlayer == null`.)
- `BeginSettling`: validates camera/ragdoll/zombie-allowed, cancels any pending settle coroutine, sets `_isSettling=true`. Deliberately does *not* call `ActivateRagdoll`/`TakeOver`/freeze anything - the corpse keeps running whatever physics it already had (EFT's own, untouched) so it can actually relax on its own.
- `TickSettling` (every `Update` while settling): polls `IsRagdollCalm` - every non-kinematic body's velocity/angularVelocity under `SettleVelocityThreshold`/`SettleAngularVelocityThreshold` - and accumulates `_calmElapsed`, reset to 0 the instant anything exceeds it. Once calm for `RequiredCalmSeconds` continuously (not just one lucky frame), or `MaxSettleWaitSeconds` elapses regardless (safety cap so an edge case that never reads calm doesn't brick the drag forever - that's exactly how the earlier `_isPhysicsDone`-gated deny approach failed), calls `EngageDrag`.
- `EngageDrag` (was `Capture`): snapshot spawner poses → `CorpseWeaponLink.Detach` → `ActivateRagdoll` → `RagdollJointStability.Capture` → per body: restore pose, zero vel, constraints None, kinematic false, `CollisionDetectionMode.Continuous`, WakeUp, `EnsurePhysicsStepped` → `FindChestBody` → record initial positions → `CreateHand`. Unchanged from the original joint-tether design except for what already ran during settling.
- `ActivateRagdoll`: not done & no missing body → `WakeUp()` then `TakeOver`; else `_putToSleep=false`, `Start()`, restore, `TakeOver`. Safe now because the corpse's own physics already relaxed it to a joint-consistent pose before this ever runs - the flag choice (WakeUp vs Start) isn't what made grabbing safe, waiting for real calm is.
- `FindChestBody`: spawner whose `BodyPartCollider.BodyPartColliderType == RibcageUp`. Grab = chest only, always.
- Drive = kinematic hand + `ConfigurableJoint` tether (KeepMeAlive technique): hand `MovePosition(MoveTowards(..., MaxHandSpeed))` toward the camera-relative target (0.25s SmoothStep blend-in); `DriveLimbs` gives every other body its own scaled spring toward a translation-only carried target so limbs keep pace with the torso; `RagdollJointStability.RepairExcessiveSeparation(deltaTime)` each tick clamps correction speed (`MaxRepairSpeed·deltaTime`, re-applied fling/stretch fix) and snag-releases (collider→trigger 0.35s) anchors stretched past `SnagSeparation`; elbow fold clamped to 135°.
- Recovery: hold jump > `TeleportDistance` → `ShiftDragBodies(jump)`; chest > `MaxHoldError` from target → shift to target. Auto-release after `MaximumSeparationSeconds` outside interaction range.
- `OnDestroy`: destroy hand, repair, restore joints/constraints/collision mode, clamp vel ≤2/angVel ≤4, restore hands/weapon collisions, `Settlement.Schedule`, send DragStopped (unless claim denied). Null-safe if the drag is stopped while still settling (nothing was ever captured).

**Why the wait, and why not kinematic (history)**
- Original design (TraumaCore → KeepMeAlive-ported kinematic hand + `ConfigurableJoint` tether, driving live `CharacterJoint` physics, grabbing immediately) reliably exploded: 100% reproducible on headless-hosted Fika raids regardless of wait time, non-deterministic but frequent in solo.
- Root cause, confirmed via decompiled source (`D:\Git Repo\Assembly-CSharp`) + diagnostic logging across many test drags: grabbing while the corpse's own death ragdoll is still mid-collapse (`!_isPhysicsDone`, genuinely live/dynamic/possibly-interpenetrating since the moment of death, joints not yet at equilibrium) and immediately forcing `breakForce=Infinity` + raised solver iterations makes PhysX violently resolve that still-chaotic state in one burst. A corpse grabbed *after* EFT's own `WorkingCycle` finished settling it (`_isPhysicsDone==true`) never had this problem - 100% correlated across every test.
- Tried a fully kinematic rigid-carry next (every body frozen, moved as one rigid block, no joints ever touched) - zero explosion risk by construction, but **rejected**: it drags like a stiff statue, no organic ragdoll motion, which is the actual point of the mod.
- Researched KeepMeAlive's own joint-tether ragdoll (`BodyRagdoll.cs`) to see why *it* never explodes despite using the identical technique: it doesn't drag corpses. It drags a *living* bleeding-out `Player` directly via `MovementContext.DirectApplyMotion` (`DownedDragController.TickDragged`) - the joint-tether ragdoll there is purely cosmetic (remote-observer-only, never authoritative, explicitly skipped on headless: `if (FikaBackendUtils.IsHeadless) return`), and it only ever starts from a calm, living, animated pose with a small controlled tip velocity (`TipForward`) - never a body that just absorbed a death impulse mid-collapse.
- Corpses have no `MovementContext`/calm-by-construction starting state to rely on, but the fix is the same one that already worked in testing: don't grab until actually calm. Replaced the fragile `_isPhysicsDone`-flag gate (which could stay false forever under some hosting conditions and bricked the drag entirely when tried) with `BeginSettling`/`TickSettling`'s own velocity-polled wait, capped so it can't hang forever either.

**EFT internals (verified from `D:\Git Repo\Assembly-CSharp`)**
- `CorpseRagdoll.Start()`: spawner `Create()` joints+rigidbodies (idempotent — no-op if the component already exists), kinematic false, `PhysicsExtensions.UpdateController.SupportRigidbody`, `_isPhysicsDone=false`, starts `WorkingCycle` if `_putToSleep`.
- `WorkingCycle`: 1s wait, loop: `TryPutToSleep` on `_rigidbodySleepHierarchy` (sticky `MustBeSleeping`), exit when `_checkCorpseIsStill(allSleeping, t)` = `sleeping || t ≥ 15s` → `StopRigidbody` all (kinematic, Discrete, **unsupported**), `_isPhysicsDone=true`, `OnRigidbodyStopped` → wait while `_isVisibleTest()` → `GetPartsTogether` → `Remove()` joints/rigidbodies (`_keepRigidbody` false for real corpses).
- EFT physics: `Physics.simulationMode=Script`, `Physics.Simulate` in Update (SmoothSimulate — a *moving average* of deltaTime, not raw per-frame), only while ≥1 supported active rigidbody. `autoSyncTransforms` off. A kinematic body's `MovePosition`/`MoveRotation` only takes visible effect at that Simulate step, same registration requirement as a dynamic body.
- `CreateStillCorpse` (raid-load corpses) → `HasRagdoll=false` → `Ragdoll` null → not draggable.

**Fika (`BodyDragFika`)**
- Loaded by `Plugin.TryLoadFikaBridge` only if `Chainloader.PluginInfos` has `com.fika.core` and `BodyDragFika.dll` beside main DLL.
- Events: `FikaGameCreatedEvent` (Active, IsHost=`FikaBackendUtils.IsServer`, HeadlessHost=`IsHeadlessGame||IsHeadless`), `FikaGameEndedEvent`, `PeerDisconnectedEvent` (host frees claims; `e.Peer` name **unverified**). Packet registration retried via `BodyDragSync.Tick`.
- Host = claim authority, first claim wins (`Claims: profileId→NetPeer`, null = host). Client→host→relay `SendData(..., peerToIgnore)`. Deny broadcast (no unicast API), only requester reacts.
- Pose: per-bone pos+rot, variable count, `Sequenced`, ~15Hz (~6KB/s/drag). Stop: ReliableOrdered with final pose. Headless: `HeadlessApplyFinalPoseOnly`.
- `RemoteCorpseDragFollower`: find corpse by `PlayerProfileID` (ObservedPlayersCorpses values, fallback `FindObjectsOfType` w/ 0.5s miss cooldown), bails (same miss-cooldown) if `CorpseDragController.IsDragging(corpse)` is already true locally — guards against a host relay/broadcast self-echoing a dragger's own pose packets back onto the same client, which would fight the local per-frame joint-driven drag with a stale ~15Hz network pose (constant jitter, since fixed). Otherwise: `TakeOver`, detach weapon, `ApplyTransformSync`, 1.5s stale timeout → release.

## Config (`Plugin.cs` defaults)

| Key | Default | Note |
|---|---|---|
| Enable Body Dragging | true | |
| Max Hand Speed | 4.5 m/s | tether pace |
| Hold Slack | 0.05 m | tether limit |
| Teleport Distance | 2.5 m | |
| Max Hold Error | 2 m | |
| Grab Spring | 8000 | user-set; limb-assist baseline only |
| Max Grab Acceleration | 8000 | user-set; limb-assist clamp |
| Held Distance Multiplier | 0.5 | user-set |
| Max Separation Seconds | 5 | |
| Limb Follow Strength | 0.5 | user-set |
| Drag Speed Multiplier | 0.2 | |
| Unequip Hands While Dragging | true | via `SetEmptyHands`/`TryProceed` (Fika-safe) |
| Allow Zombie/Bot Corpses | true | |
| Debug Logging | false | |
| Headless Applies Final Pose Only | true | |
Removed: Grab Damping, Lock Aim While Dragging. (The kinematic redesign had briefly dropped Hold Slack/Max Hold Error/Grab Spring/Max Grab Acceleration/Limb Follow Strength too - restored along with the joint-tether drive.)
No new config for the settle-wait yet: `SettleVelocityThreshold`(0.3 m/s)/`SettleAngularVelocityThreshold`(1 rad/s)/`RequiredCalmSeconds`(0.3s)/`MaxSettleWaitSeconds`(3s) are consts in `CorpseDragController.cs`, not exposed - candidates to bind if the defaults need tuning per feedback.

## Changelog (chronological)

1. Scaffold + port TraumaCore rip list, Fika bridge, LICENSE/NOTICE.
2. `InternalsVisibleTo("BodyDragFika")` — bridge used internal members (compile fix).
3. Perf: follower `FindObjectsOfType` miss-cooldown.
4. Feedback: speed 0.5→0.2; spring/damping/accel retuned; blend 0.75→0.25s.
5. AUDIT.md (physics audit of TraumaCore drag — now outdated: describes the removed spring/joint drive).
6. Aim lock — added then **reverted** per user.
7. Perf: WakeUp loop → only grabbed body (moot after the kinematic redesign).
8. `DriveLimbs` limb-assist spring, then KeepMeAlive-ported tether/snag-fix/teleport-recovery (both removed in the kinematic redesign).
9. First-drag-explosion investigation (long saga, condensed): Harmony `WorkingCycle` wrap failed (Mono inlining) → field-swap takeover (`CorpseRagdollSettlement.TakeOver`, kept) → `EnsurePhysicsStepped` registration fix (real but secondary bug) → extensive diagnostic logging across a dozen+ test drags nailed the real root cause (see "Why the wait" above).
10. Headless report: 100% of drags explode regardless of wait (vs. solo's "sometimes, less if you wait") — same root cause, just deterministically hit every time. Prompted researching KeepMeAlive's actual drag mechanic for comparison.
11. `RemoteCorpseDragFollower` self-echo jitter bug found + fixed (constant fast jitter on Fika, reported separately, same session) - kept through every later revision, design-independent.
12. Tried a **kinematic rigid-carry** redesign (every body frozen, moved as one rigid block) - confirmed zero explosion risk, but **rejected by user**: no organic ragdoll motion, looked like dragging a statue. A `WouldClipWorld` world-clip-detection feature built for that design (and its one bug fix, a missing self-collider exclusion) is gone along with it - never shipped.
13. Settled on: restore the original joint-tether drive exactly as it was (hand+`ConfigurableJoint`, `DriveLimbs`, `RagdollJointStability`, including the deltaTime-capped fling/stretch fix), gate it behind a new `BeginSettling`/`TickSettling` velocity-polled wait instead of grabbing immediately.
14. Headless-hosted Fika raid report: `Begin` always did nothing, for real human clients too. The `BodyDragSync.HeadlessHost` guard added in the KeepMeAlive research pass was the cause - `IsHeadlessGame` (half of that flag) is true for every client connected to a headless-hosted raid, not just the headless process itself. **Reverted** that guard; the real "no human player" case was already covered by the existing `GamePlayerOwner.MyPlayer == null` check further down `Begin`.
15. Headless report, round 2: corpse physics still explodes on grab *even after the settle-wait confirms it's calm*. Root cause (new): a Fika **client** (never the host - headless occupies that role) sees every corpse it didn't simulate itself as an `ObservedCorpse` (`ObservedPlayer.CreateCorpse`, `Assembly-CSharp`), not a plain `Corpse`. `ObservedCorpse` carries EFT's own *native* multiplayer corpse-position sync (`ApplyNetPacket`/`MoveTo`, driven by `ClientWorld.SyncCorpses` processing the host's periodic `GameWorldPacket.CorpseSyncPackets` - this is BSG's own system, not Fika's; Fika's own `CorpsePositionSyncer`/`CorpsePositionPacket` exist in `Fika-Plugin` but are dead/commented-out code in the checked-out build, not the cause). `MoveTo` nudges the pelvis toward the host's authoritative position and calls `Ragdoll.WakeUp()` whenever the gap exceeds `CorpseSyncThreshold` - this keeps running the *entire* time we're settling or dragging, completely unaffected by `CorpseRagdollSettlement.TakeOver` (which only touches `WorkingCycle`'s fields, not this). A sync packet landing mid-settle or mid-drag re-wakes and repositions bodies out from under us, reproducing the explosion regardless of how long we waited first. Explains both why it's headless-specific (as a client you're never host, so *every* corpse you drag is an `ObservedCorpse`) and why the wait didn't help (the next packet can always land after it).
16. **Fix:** new Harmony prefix `Patches/ObservedCorpseNetSyncPatch.cs` on `ObservedCorpse.MoveTo` - no-ops (skips the pelvis shift and `WakeUp()`, returns "already within threshold") whenever `CorpseDragController.IsDragging(__instance)` is true. Suppressed only for the exact corpse we're actively claiming (covers the settle-wait too, since `IsDragging` is true from `Begin()` onward) - EFT's native sync resumes normally the instant we release it, for everyone else's view. `ApplyTransformSync`/`ForceApplyTransformSync` (the "final pose" snap paths, used once the host marks a corpse done+still) are a residual risk, not yet patched - lower priority since they're one-shot rather than continuously recurring.
17. Checked `D:\Git Repo\Fika-Headless` (added to global CLAUDE.md refs) for anything else headless-specific that could be fighting the drag - zero hits for `Corpse`/`Ragdoll`/`ObservedCorpse`/`CorpseSync`/`GameWorldPacket`/`Physics.Simulate` anywhere in that repo. One candidate ruled out: `FikaHeadlessPlugin.ToggleFramelimit` drops to `Application.targetFrameRate=1` while idle/lobby (`OnReady`), but switches back to the configured `UpdateRate` (default 60) the moment a raid actually starts (`ToggleFramelimit(false)`) - not in effect during actual gameplay/dragging, so not a contributor.
18. Still exploded after rebuild with the `ObservedCorpse.MoveTo` fix live (confirmed enabled in log) - same signature (`path=WakeUp`, `physicsDone=False`, snags into the thousands). Log also showed a patch named `ObservedCorpseSleepPatch` enabled - not ours, not anywhere in this codebase. Theory: a third-party mod forces corpses kinematic/"asleep" very fast after death without going through EFT's own settle path (`_isPhysicsDone` never flips, matching every log) - our `IsRagdollCalm` skips kinematic bodies as trivially calm, so the settle-wait approves almost instantly, but the frozen pose is whatever violent configuration existed the moment that mod froze it. `EngageDrag` then force-sets `isKinematic=false` and engages full dynamic joints on that still-strained pose - same explosion, different trigger.
19. **Confirmed. User disabled the conflicting mod - drag works.** Our mod is correct in isolation; root cause was a third-party mod's own `ObservedCorpse` sleep/freeze patch defeating our settle-wait's velocity check by making an unsettled corpse read as already-kinematic-and-calm.
20. User shared the conflicting mod's patch: `ObservedCorpseSleepPatch`, a Harmony postfix on `ObservedCorpse.CheckCorpseIsStill(bool sleeping, float timePass)` that replaces `__result` via its own `RagdollCoordinator.ShouldStop(...)` - almost certainly a ragdoll-count/perf budget system that force-freezes corpses on its own schedule, independent of whether they're actually settled. That return value is exactly what EFT's `WorkingCycle` loop reacts to (`while (!_checkCorpseIsStill(...))` → exit → `StopRigidbody`, kinematic) - so this mod can freeze a corpse mid-flail, and `IsRagdollCalm` reads the resulting kinematic body as trivially calm.
21. **Compat fix:** new `Patches/ObservedCorpseStillnessPatch.cs` - Harmony postfix on the same `ObservedCorpse.CheckCorpseIsStill`, forcing `__result=false` whenever `CorpseDragController.IsDragging(__instance)` is true (same claim window as the `MoveTo` patch - covers settling too). Generalizes the "we claimed this corpse, nothing else gets to freeze it" principle to this second mechanism. Tagged `[HarmonyPriority(Priority.First)]` so it always runs last among postfixes on that method and wins, regardless of plugin load order relative to the other mod. Not yet build-verified - this is a response to seeing the other mod's code, not a retest.

## Known gaps / backlog

- Not rebuilt or tested with the settle-wait + real joints combination yet, including on headless — next thing to verify.
- `SettleVelocityThreshold`/`RequiredCalmSeconds`/`MaxSettleWaitSeconds` are first-guess constants, unvalidated beyond the confirmed-working test - may need tuning if a very violent death still slips through.
- `IsRagdollCalm` still treats any kinematic body as trivially calm (skip, don't fail) - unchanged. Addressed the known case (`ObservedCorpseSleepPatch`) at the source instead via `ObservedCorpseStillnessPatch` (force `CheckCorpseIsStill=false` while we've claimed the corpse, so nothing can freeze it out from under the wait in the first place). A mod that freezes corpses through some OTHER mechanism entirely (not `CheckCorpseIsStill`, not `ObservedCorpse.MoveTo`) would still defeat `IsRagdollCalm` the same way - not yet generalized further than these two known entry points.
- Not build-verified by agent generally; `PeerDisconnectedEvent.Peer` unverified.
- No player-facing feedback while settling yet - only a `BodyDragLog.Info` (gated behind Debug Logging). If the wait is noticeable, "drag did nothing" complaints could recur; worth a visible cue if so.
- Snag release: fixed 0.35s timer, no overlap check before re-enable (KeepMeAlive uses `ComputePenetration` - considered porting that during the kinematic detour, not carried over since the kinematic design that motivated it was dropped).
- Extremities share `MaximumGrabAcceleration` with limbs — possible whip.
- Fika: no pose interpolation on peers (snap at ~15Hz); no delta-suppression; per-packet array allocs.
- Taken-over corpses: EFT cleanup deferred until handback.
- Plan §5 items: ObservedCorpse finale coroutines vs forced poses, late-joiner resync, loot-open while dragged, mod-missing peers.
- AUDIT.md outdated re: drive mechanism (describes the long-removed spring/joint drive).

## Conventions / user prefs

- Caveman Ultra replies + docs.
- No LINQ in client per-frame paths (memory: `feedback_spt_client_linq.md`); one-shot OK; server mods exempt.
- Don't bandaid; best-of-both, straightforward. Plan first when asked.
- Respect user's on-disk edits (config defaults, csproj SptRoot).
- Research the actual reference mod's code before assuming a technique transfers — KeepMeAlive's joint-tether ragdoll looked identical to ours but solves a different problem (cosmetic-only, living player, calm starting pose); confirm domain fit before porting a technique, not just the code shape.
