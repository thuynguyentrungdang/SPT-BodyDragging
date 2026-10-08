# BodyDragging

Drag corpses and downed teammates in SPT 4.1 (Single Player Tushonka). Real ragdoll physics, Fika co-op sync, optional Rupture support.

## Features

- **Corpse drag.** Corpse menu → `DRAG BODY`. Chest tethered to a kinematic hand; limbs follow, joints repaired when snagged.
- **Downed-teammate drag.** Fika revive menu → `DRAG BODY` / `RELEASE`. Downed player walks with you, ragdoll pulled on every peer, `Revive` greyed while dragging. Revive afterwards stands them up where dragged.
- **Fika sync.** Optional bridge, loads only if Fika present. Claims, poses, release, disconnect handled.
- **Rupture support.** Rupture-managed corpses dragged through Rupture's corpse-drag ABI (host-authoritative). Standalone physics otherwise.
- **Feel.** Slowed movement while dragging, weapon stowed and re-equipped, auto-release on distance.
- **Tunable.** Hand speed, grip, limb spring, held distance, speed penalty. All in BepInEx config.

## Requirements

| | |
|---|---|
| SPT | 4.1.x (Tushonka), BepInEx 5 |
| Fika | Optional. 2.4.x. Same mod build on every peer + headless host |
| Rupture | Optional. 1.1.6 (ABI V1). Same version everywhere |
| Downed drag | Fika **server revive enabled**. Not compatible with KeepMeAlive (needs Fika revive off) |

## Install

1. Download latest `BodyDragging-x.y.z.zip` from [Releases](https://github.com/thuynguyentrungdang/SPT-BodyDragging/releases).
2. Extract into SPT root. Result: `BepInEx/plugins/kobethuy-BodyDragging/` with `BodyDragging.dll`, `BodyDragFika.dll`.
3. Fika raid: install on **every** client and the headless host. Mismatched builds fail Fika's mod check.
4. Start game. Config appears at `BepInEx/config/com.kobethuy.bodydragging.cfg`.

No server mod. `BodyDragFika.dll` is not a plugin; the main plugin loads it only when `com.fika.core` is present.

## Usage

**Corpse**
1. Look at corpse within interaction range → `DRAG BODY`.
2. Walk. Body follows, you move slower (`Drag Speed Multiplier`).
3. `STOP DRAGGING` in menu to release. Auto-release after `Max Separation Seconds` out of range.

**Downed teammate** (Fika revive on)
1. Look at downed player → `DRAG BODY`. Weapon stowed.
2. Walk away > 1.2 m. They follow, trailing behind you.
3. Look at body → `RELEASE`. Weapon returns. Then `Revive` as usual.
4. Auto-release: 7 m leash, either player down/dead/disconnected, 9 s without heartbeat.

## Config

`BepInEx/config/com.kobethuy.bodydragging.cfg`. Physics/Rupture values are read on the machine that simulates (host/headless for Rupture corpses); set the same on every client.

| Section | Key | Default | Note |
|---|---|---|---|
| General | Enable Body Dragging | true | |
| General | Drag Speed Multiplier | 0.2 | Applies to corpse and downed drag |
| General | Unequip Hands While Dragging | true | Corpse drag; restored after |
| General | Allow Zombie/Bot Corpses | true | |
| Physics | Max Hand Speed | 4.5 | m/s |
| Physics | Hold Slack | 0.05 | m, smaller = firmer |
| Physics | Teleport Distance / Max Hold Error | 2.5 / 2 | Recovery thresholds |
| Physics | Grab Spring / Max Grab Acceleration | 8000 / 8000 | Limb assist |
| Physics | Limb Follow Strength | 0.5 | 0 = joints only. Raise for heavier feel |
| Physics | Held Distance Multiplier | 0.5 | |
| Physics | Max Separation Seconds | 5 | |
| Rupture | Remote Target Smoothing | 0.08 | s, remote draggers only |
| Rupture | Ground Follow Hold | true | Hold height follows terrain |
| Rupture | Hold Pose Yaw Follow | 0 | Experimental, can twist |
| Rupture | Head Leads / Head Lead Turn Rate | false / 120 | Experimental, head faces you |
| Fika | Headless Applies Final Pose Only | true | Standalone path only |
| Debug | Debug Logging | false | Timing/cadence logs |

Note: BepInEx keeps old values when a default changes. Edit or delete the cfg entry.

## Known limits

- Rupture-managed corpses: smoothness capped by Rupture. Headless host simulates 30 Hz; graphical host steps per frame.
- Downed drag: no hand animation. Dragger crouch/stance not enforced.
- Mods forcing death on `IsAlive == false` players (e.g. SAIN dead-bug rescue) break Fika's downed state. Exclude Fika-downed players there.
- Never tested on SPT builds other than 4.1.x.

## Build

Needs .NET SDK 10 (`.slnx`), an SPT 4.1 install (reference DLLs).

```powershell
dotnet build SPT-BodyDragging.slnx -c Release -p:SptRoot=D:\SPT -p:SkipDeploy=true -p:SkipPackage=true
```

- `SptRoot` default `F:\SPT_4.1`. Needs `Assembly-CSharp`, `UnityEngine*`, `Comfort`, `Sirenix.Serialization`, `0Harmony`, `BepInEx`, `spt-common`, `spt-reflection`, `Fika.Core`.
- Debug build (no `SkipDeploy`) copies DLLs to `<SptRoot>\BepInEx\plugins\kobethuy-BodyDragging`.
- Package zip: `scripts\Package-Release.ps1` → `dist\BodyDragging-<version>.zip`.
- Tests (math, wire format, ABI adapter): `scripts\Test-RuptureIntegration.ps1 -SptRoot <SPT> -RuptureDll <Rupture.dll> [-PhysXBackendDll <..> -NativeBridge <..>]`. No automated EFT/Fika tests; verify in game.

## Release

GitHub Action `.github/workflows/release.yml`: push tag `vX.Y.Z` (must equal `<Version>` in `SPT-BodyDragging.csproj`) or run manually. Builds, zips, publishes Release.

CI has no game files. Repo secret `SPT_LIBS_URL` = direct URL of a private zip with the reference DLLs in SPT layout (`EscapeFromTarkov_Data/Managed/*`, `BepInEx/core/*`, `BepInEx/plugins/spt/*`, `BepInEx/plugins/Fika/Fika.Core.dll`). Do not commit game DLLs.

```
bump version → commit → git tag v1.0.0 → git push origin v1.0.0
```

## Contributing

1. Fork, branch from `master`, PR back.
2. Main assembly stays Fika-free. Fika/Rupture code goes in `BodyDragFika` or behind reflection.
3. No LINQ/allocations in per-frame client paths.
4. Run harness before PR. Note live-test result in PR.
5. Log decisions in `DEVLOG.md`.

Layout: `SPT-BodyDragging/` plugin · `BodyDragFika/` Fika bridge · `tests/` harness · `scripts/` · `docs/` Rupture ABI notes.

## Credits & license

Apache-2.0 (`LICENSE`, `NOTICE`). Corpse drag derived from [TraumaCore](https://github.com/Hysocs/traumacore-spt) (Hysocs, Apache-2.0). Downed drag technique from [KeepMeAlive](https://github.com/awnova/KeepMeAlive) (MIT). Rupture integration by rootdarkarchon. Built on SPT, BepInEx, HarmonyX, Fika.
