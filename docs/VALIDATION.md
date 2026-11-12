# Validation

Status: preview 0.1.0.2. Host lifecycle, public discovery,
vanilla joining, visible server chat, and rejoining after restart verified.
Vehicle, item, paint, and imported-player persistence checks remain outstanding.

The preview version replaces the unpublished local 1.x version labels. It adds
release metadata and rules; the runtime findings below were collected on the
same hosting behavior before that version-label change.

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

- Supervisor and plugin build with zero warnings/errors. All 37 supervisor tests
  pass. Supervisor regression
  checks cover config, imports, locks, completed-save backups, retention, crash
  budgets, warning schedules, custom messages, and concurrent atomic IPC reads.
  Follow-up coverage includes public JSON round-tripping, CLI input rejection and
  overwrite protection, disabled schedules, overdue polls, and fractional countdowns.
  A delayed-poll regression exposed stale restart warnings; countdowns now use
  actual remaining time instead of the previously crossed threshold.
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

### Public lobby and damaged-save follow-up

On November 12, 2026, an isolated host configured with `lobby.public: true`,
four human slots, and a test password reached world readiness with zero humans.
It completed a scheduled backup, a manual backup, and a save-and-stop operation.
This proves public-mode startup, not discovery or joining from a vanilla client.
The staged 1.0.1.1 build then reopened the same world as a public lobby with an
empty password, completed another verified backup, and saved and stopped cleanly.
Both public runs reported zero humans. All task-owned game processes were stopped.

The inspected game code publishes `game_mode=default`, `is_public=1`, and the
game version. Its public browser searches for those same attributes, including
an exact version match (`networking.cs:4627,5191,5203,5215` in the retained
decompile). Public visibility and passwords are independent.

A copied fixture containing real saved-world metadata and deliberately damaged
`save.dat` bytes failed startup with `Cannot load saved world: Damaged`. No lobby
or ready session was established. Recovery was disabled and the process exited.
SHA-256 comparison confirmed the source and imported damaged bytes were unchanged.
This does not cover a readable save whose individual objects partially fail restore.

Starting the user's
ordinary client can reconcile local and Steam Cloud saves from the main menu,
before joining (`game.cs:92333`; `evilsave.cs:1187,1211,1226`). Changing only the
Unity company/product storage path does not isolate the cloud account. An untouched
client needs a separate test profile/account, or permission for normal cloud sync.
A client patched only to isolate storage would need to be reported as modified.

### Unmodified Steam client acceptance

On November 12, 2026, Keagan authorized using his normal Steam client and normal
cloud reconciliation. Before launch, all 12 local EvilSave files were archived and
their SHA-256 hashes checked against the ZIP. That backup is retained outside
scratch under `Documents/NomadDrive-Backups/2026-11-12-before-client-test/`.
Keagan manually approved the Windows firewall prompt.

The client ran from the original Steam installation. Its Assembly-CSharp hash
matches the supported build above; no BepInEx directory or Doorstop winhttp.dll
was present. The vanilla Join Game browser listed `Odin Public Test`. Clicking
that row successfully joined the generated world. Two other public players also
connected, bringing the host's observed count to three humans. Another player's
movement was visible. Those other clients' installation contents were not checked.

Scheduled backup start/completion messages and an administrative ten-second
restart notice rendered in the vanilla chat UI. The manual restart completed its
save, shut down the old process, launched a new process, and reopened the same
slot. The client showed its normal lost-host dialog, then rediscovered the public
lobby and rejoined successfully. Restart creates a new lobby join code; the client
does not automatically reconnect. The returning player's saved position differed
by only 0.0000726 meters vertically after settling, with identical X/Z coordinates.

Keyboard taps could operate menus and chat, but the available UI automation API
does not expose held movement keys. Driving, picking up items, attaching parts,
and applying paint/wash strokes have not been exercised by the operator. The
client and test host were initially left running for Keagan's gameplay handoff.
Keagan deferred those checks. At 19:17 UTC on November 12, a final verified backup
completed, graceful shutdown saved successfully, and process inspection confirmed
the isolated host had exited. The normal Steam client was left open. The remaining
checks below are still untested. No production server was deployed.

### Remaining acceptance checks

- Driving, loot/inventory interaction, and vehicle attachment/removal.
- Nonempty player inventory persistence through save, restart, and rejoin.
- Imported host inventory and position restoration.
- Player-driven paint/wash changes surviving save, restart, and rejoin.
- Failed/partial imported restoration exercised against actual game data.
- Multiple clients in separate terrain regions and extended play. Three simultaneous
  human connections alone do not establish these behaviors.
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
Initial host-only test processes were stopped before client acceptance.
Earlier temporary public validation lobbies were opened and closed; the later
client acceptance handoff is described above. No persistent public server was
deployed. Follow-up fixtures and concise evidence
are retained under `E:/Odin/scratch/nomad-drive/public-acceptance/` for reproduction.
