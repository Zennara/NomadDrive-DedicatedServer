# Validation

Status: local development candidate 1.0.0.0. Isolated host lifecycle verified;
vanilla-client gameplay verification is still outstanding.

Verified game input: Steam build 26391437, Unity 6000.0.73f1.
`Assembly-CSharp.dll` SHA-256:
`6957F9A1180D811CA2AE1BB44748157D794FED5BEFD845290FD15E41FB54FFAF`.

## Static evidence

- `EOSLobbyManager.StartMirrorHostAsync` loads the game scene, then starts a
  Mirror host. `WorldGenerator.OnStartClient` initializes MapMagic and road
  generation. A bare `StartServer` misses these systems.
- `GameSaveService.SaveAndWaitAsync` awaits `EvilSaveManager.SaveGameAsync`,
  returns false on failure, and exposes `LastSaveSucceeded`. Its completion
  event also runs on failure, so the event alone is insufficient for backups.
- `MirrorNetworkManager.OnServerAddPlayer` normally creates the host avatar.
  The adapter leaves the local client ready but suppresses its player request.
- `GameSaveService.RunRestoreAsync` treats HostPlayer separately from RemotePlayers.
  Imports preserve the original host record as a remote player record.
- `WorldGenerator.WorldReadyHardSafetyCoroutine` can claim readiness after
  45 seconds without successful terrain. The adapter also requires a completed
  non-draft terrain tile and successful restore.
- [EOS lobby limits](https://dev.epicgames.com/docs/epic-online-services/multiplayer/lobbies-and-sessions/lobby-interface/lobby-interface-reference)
  establish 64 members, not 64 supported human players for this implementation.

## Runtime loader findings

Stock BepInEx 5.4.23.6 fails against the game's stripped core library:

```text
System.MissingMethodException: Method not found: void System.Reflection.Module.GetPEKind(System.Reflection.PortableExecutableKinds&,System.Reflection.ImageFileMachine&)
```

The `unity.bepinex.dev/corlibs/6000.0.73.zip` archive is not a working Windows
replacement. Testing it produced:

```text
DllNotFoundException: System.Native assembly:<unknown assembly> type:<unknown type> member:(null)
  at (wrapper managed-to-native) Interop+Sys.LChflagsCanSetHiddenFlag()
```

[UnityDataMiner issue 3](https://github.com/BepInEx/UnityDataMiner/issues/3)
documents the platform-specific profile problem. Use the matching Windows
profile, not that archive.

The exact official Unity 6000.0.73f1 editor archive contains the Windows profile
at `Editor/Data/MonoBleedingEdge/lib/mono/unityjit-win32`, with .NET Standard under
`Facades/netstandard.dll`. Setup checks archive and extracted profile hashes.

## No-graphics limitation

The null graphics device initially failed MapMagic height application with
`InvalidOperationException: Active RenderTexture is null.` Selecting its built-in
CPU `SetHeights` path permitted terrain generation, but does not solve saving
paint/restoration state. `RestorableVehicleSurface.RefreshPngCacheAsync` and
`GetPngCached` read rendered textures. Ordinary clients relay strokes rather than
uploading authoritative PNG snapshots; the game's client surface upload path is
disabled. Weathering also changes these surfaces while no players are connected.

The production adapter therefore rejects a null graphics device. The supervisor
rejects `headless:true`. Tested graphics-enabled batch mode created no visible
game window (`MainWindowHandle == 0`) and did not produce the headless texture
exceptions. GPU-backed paint persistence still needs an actual client test.

## Automated and host runtime results

On Windows, November 12, 2026:

- Supervisor and plugin build with zero warnings/errors. All 29 supervisor tests
  pass. Supervisor regression
  checks cover config, imports, locks, completed-save backups, retention, crash
  budgets, warning schedules, custom messages, and concurrent atomic IPC reads.
- BepInEx loads the plugin. EOS device authentication and private lobby creation
  succeed without a Steam player host.
- The host reaches readiness with zero human players, completed non-draft terrain,
  and no playable host avatar.
- Manual save succeeds. Backup creates and verifies an archive, then removes the
  archived staging snapshot. Server chat RPC accepts the broadcast request.
- Graceful restart saves, exits, launches a new process, and reopens the same slot.
- Import copies an existing test world to a new data directory, loads it, and
  preserves the source. No original user save was used for testing.
- A lobby configured for 63 humans reaches readiness. This confirms a 64-identity
  lobby can be created with voice disabled, not that 63-player gameplay is stable.
- Live testing caught a Windows file-sharing race. IPC readers now allow atomic
  replacement and retry transient sharing conflicts. A 500-replacement concurrent
  reader regression test verifies the fix; subsequent live restart tests passed.
- The exact-profile final run created a custom world with one human slot and
  completed a scheduled one-minute backup. After a forced crash it reopened the
  same world; a second forced crash stopped at the configured one-retry budget.
- Repeated backups with retention set to two left exactly two verified archives.
- A fresh runtime created entirely by `setup-runtime.ps1`, followed by
  `build.ps1`, successfully loaded the saved test world through the packaged CLI.
  All test hosts were stopped after validation.

The first loader probe used an already installed Windows Mono profile from Unity
6000.0.75f1. Its mscorlib, System, and System.Core hashes match the exact 6000.0.73f1
Windows editor profile. Final validation uses only the exact editor profile.

## Required runtime checks

- Vanilla client join, driving, loot interaction, chat warnings, and reconnect.
- Imported host inventory and position restoration.
- Player-driven paint/wash changes surviving save, restart, and rejoin.
- Failed/partial imported restoration exercised against actual game data.
- Multiple clients in separate terrain regions and extended play.
- Gameplay capacity above the default four players.

Until recorded as passed below, these remain unverified.

## Local test workspace retained

`E:/Odin/scratch/nomad-drive/dedicated-server/` retains:

- `runtime-final/`: approximately 3.01 GiB, prepared by the tested setup script,
  with the current hosting plugin. Kept for a vanilla-client acceptance test.
- `downloads/`: approximately 4.23 GiB, the pinned official Unity/BepInEx archives
  and verified Windows profile. Reusable setup cache; safe to retire when no
  further runtime preparation is needed and downloading again is acceptable.
- Test worlds, small regression fixtures, lifecycle logs, and `final-evidence.json`
  document the probes. They contain no original user saves. Logs remain local
  because game diagnostics may include online identity information.

Manifest-verified cleanup removed approximately 8.33 GiB of superseded installers,
incompatible libraries, extraction probes, build cache, and the obsolete runtime.
All task-owned game processes were stopped. No public deployment was performed.
