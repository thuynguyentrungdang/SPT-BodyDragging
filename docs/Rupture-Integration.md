# Rupture V1 integration test handoff

BodyDragging branch: `codex/rupture-drag-abi`, based on upstream commit `7b5dc200d7c87d9f8630a39cfe3ecb95cde965f6`. This patch builds BodyDragging and BodyDragFika version 1.0.1. Target: SPT 4.1.6 / EFT 0.16.9.40743, Fika 2.4.3 and Rupture 1.1.6. Use the same mod versions on the graphical host, headless server and all clients.

## What changed

The main plugin discovers `Rupture.Integration.CorpseDragV1` through an optional reflection adapter. It has no assembly reference to Rupture or Fika. A soft BepInEx dependency ensures Rupture initializes before discovery. Missing/incompatible ABI or unavailable managed ownership blocks acquisition; only an explicit `NotManaged` result selects native dragging. Rupture absence keeps the original standalone physics route.

Managed dragging retains the local camera/yaw target, distance blend, capture height, separation watchdog, movement penalty and hands handling. It never reboots EFT ragdolls, changes their native constraints/joints/colliders, detaches their weapon or takes over settlement. The observed-corpse suppression patches are limited to native drags, and the native pose follower rejects managed corpses.

Solo runs the authority controller directly. Fika sends begin, target/heartbeat and end intent to the host. There are no managed bone-pose packets. The host associates each accepted claim with a local ABI lease and the exact session/profile/death identity. Input and end must match the originating peer. Queued callbacks recheck ownership on the plugin's main thread; callbacks from an earlier raid are dropped. Denials go only to the requesting peer.

The authority controller runs from `Plugin.Update` on graphical and headless hosts. It has no camera or local-player dependency. `HeadlessApplyFinalPoseOnly` applies only to the standalone native pose route. Rupture continues publishing corpse motion to observers through its own Fika stream.

The authority waits briefly for calm using actual solver velocities, with a three-second wait cap. The caller sends heartbeats during this preparation. No fresh input for two seconds releases the lease; preparation is capped at six seconds. Release keeps the claim reserved until the ABI reports completion, with a bounded fallback if the solver cannot progress. Disconnect, game exit and shutdown release authority sessions. Native activation rejection does not schedule native settlement.

Physics settings are captured from the host at acquisition. Limb assistance uses completed Rupture positions/velocities and surviving original core indices. A backward-Euler PD calculation replaces the explicit limb spring on the managed route, while retaining the spring/damping intent and acceleration cap. The force is an acceleration, without an extra caller delta-time multiplier. The step estimate uses the completed solver duration, elapsed authority update time and a conservative 30 Hz minimum. Older V1 providers without the additive timing field use the update-time fallback.

Relocation uses a common core/hand offset through the ABI. Only one recovery id can be pending; it is retained through target coalescing and retired only after `LastTranslationId` acknowledges it. Newly severed bodies are excluded on each read. Grip loss ends the session. Rupture retains weapon, gore and detached-body ownership.

## Rupture follow-ups

Rupture 1.1.6 adds `DragView.SimulationDeltaTime` in seconds, covering the step identified by `CompletedStep`. Existing V1 readers remain compatible. It also promotes an unassigned rig death identity when the exact corpse/ragdoll receives its authoritative sequence; an already assigned rig or active lease is never rebound. Networked corpses with an unassigned sequence cannot acquire a drag lease.

The public ABI version remains 1, native ABI 10 and Rupture Fika protocol 8. The full Rupture patch in the source handoff includes the earlier ABI implementation as well as these follow-ups, and targets Rupture commit `6bdb68dc7f30eadf6e7b5f74919c9bd6b66a3332`. Do not apply that full patch on top of an already modified 1.1.5 checkout; use the supplied canonical 1.1.6 package for testing instead.

## Apply and build

In a BodyDragging checkout at the pinned upstream base:

```powershell
git switch -c test/rupture-drag-abi
git am <path-to-BodyDragging-0001-patch>
dotnet build SPT-BodyDragging.slnx -c Release -p:SptRoot=D:\Tarkov-SPT-4.1 -p:SkipDeploy=true -p:SkipPackage=true --disable-build-servers -m:1
```

Replace both `BodyDragging.dll` and `BodyDragFika.dll` in the existing BodyDragging plugin directory with the matching build outputs. These build flags deliberately skip deployment and release ZIP generation. Extract the canonical Rupture 1.1.6 package into each test installation. A solo installation does not need Fika; the Fika bridge remains dynamically loaded only when Fika is present.

## Executable checks

Run from the BodyDragging checkout in PowerShell 7:

```powershell
.\scripts\Test-RuptureIntegration.ps1 -SptRoot D:\Tarkov-SPT-4.1 `
  -RuptureDll D:\Tarkov-SPT-4.1\BepInEx\plugins\rupture\Rupture.dll `
  -PhysXBackendDll D:\Tarkov-SPT-4.1\BepInEx\plugins\rupture\Rupture.PhysXBackend.dll `
  -NativeBridge D:\Tarkov-SPT-4.1\BepInEx\plugins\rupture\rpt_physx_bridge.dll
```

Without the last two parameters, the script runs the managed/packet checks only. With both, it also runs an isolated two-body PhysX fixture using Rupture's actual backend ABI definitions and native binary. The adapter test uses actual Rupture DTO types and a fixture facade; it does not instantiate an EFT corpse.

Validation: both product assemblies build with zero warnings/errors. All 85 integration checks pass, including optional loading metadata, actual V1 DTO marshalling, input validation, session/death/peer checks, main-thread dispatch, raid generation fencing, release ordering, topology changes, recovery acknowledgement, wire round trips and physical drive fixtures at 60/30/10/5 Hz. In the steady 1.5 m/s isolated pull, peak actor speed is about 1.50-1.53 m/s after the stable PD change. The original explicit assist produced about 71.8 m/s in the 60 Hz fixture. This is fixture evidence, not an EFT terrain or network-latency benchmark.

## Required live acceptance

Source/compiled checks establish the shared headless/local controller and its ownership boundaries. They cannot guarantee behavior in live EFT/Fika. No live raid was run during this handoff. Test the following before publishing compatibility:

| Setup/case | Expected result |
| --- | --- |
| Solo, Rupture present and absent | Correct provider selection; dragging, movement/hands restoration, release and re-grab. |
| Graphical Fika host, client dragging, another observer | Host runs managed physics; all peers see Rupture motion; client never drives native shells. |
| Headless host, default final-pose-only setting | Continuous managed motion still runs, with no headless camera/local-player requirement. |
| Headless under AI load | Usable motion without limb-assistance velocity spikes; record host/client logs and cadence. |
| Fresh/dormant/settled corpse, including impact reactivation disabled | Acquire at actual pose, no death impulse replay or native body reboot. |
| Long hold beyond Rupture sleep deadline | No forced settle until release; normal bounded settlement resumes. |
| Terrain snag, stairs, vault and repeated recovery | One whole-core relocation per id; no double teleport or forced reanimation of severed bones. |
| Hit/explosion/severing while held | Rupture damage and gore continue; eligibility changes take effect; grip loss releases cleanly. |
| Two simultaneous claimants, delayed/duplicate packets and immediate re-grab | One lease; wrong peer/session cannot drive or end it; claim frees after completed release. |
| Disconnect, lost end, game exit and a second raid | Bounded release, no persistent claims and no old callback affecting the new raid. |

Downed/living ragdolls, exact Unity projection parity and temporary collider/joint repair extensions remain outside this ABI. The standalone algorithms are retained for native corpses; the managed path does not emulate them by writing Rupture's shells.
