# Validation

Status: preview 0.3.3.7. The supervisor suite passes 44 checks, including descriptive
public lobby names with the Nomad preset and uncertain
late shutdown exits not being treated as crash-restart permission. Host and CLI
builds have zero warnings/errors. An isolated actual-game test with Vehicle Claiming
& Protection 0.4.0.3 completed automatic parking before shutdown, kept claims parked
after restart, canceled a test-refused shutdown without stopping or locking commands,
and completed a subsequent stop. These are host-side checks, not remote-client UI
acceptance. Long parking queues can exceed command timeouts; unconfirmed late exits
require operator review rather than automatic recovery.

Previous builds verified host lifecycle, public discovery,
vanilla joining, visible server chat, and rejoining after restart. The new spawn
policy still needs live multiplayer acceptance. Vehicle, item, paint, and
imported-player persistence checks remain outstanding.

Preview 0.1.0.2 replaced the unpublished local 1.x version labels and added
release metadata and rules. Preview 0.2.0.3 changes ordinary joining to use the
longest-connected human as the spawn reference, including returning players.

## Spawn-reference change (0.2.0.3)

The production connection-order helper is linked into the test suite. All 39
checks pass, including handover, returning connections, recycled IDs, duplicate
registration, unavailable players, self-exclusion, and empty-server selection.
The plugin and supervisor build with zero warnings/errors against the pinned
game. Server transforms are seeded before sending initial-spawn RPCs so another
queued join cannot use the previous player's uninitialized zero position.
These checks do not establish live client placement or handover.

The changed plugin created a private test world, reached ready with zero human
players, and completed a saved shutdown on November 12, 2026. This exercises
Harmony patch installation and host lifecycle, not the human spawn path. Live
simultaneous joins, delayed clients, and reference handover remain unverified.

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

### Native voice capacity investigation (2026-11-14, 0.3.2.6 candidate)

Native voice is tied to the lobby conference room. Removing the large-lobby
guard and requesting RTC for 64 identities failed before world startup:

```text
LobbyInvalidSession
Lobbies that generate conference rooms must have <= 16 max players, has 64
```

This runtime backend response establishes a 16-identity conference-lobby limit
for the tested product. The adapter reserves one for its host client, leaving 15
human slots when using native voice. A 63-human configuration retains its capacity
with RTC disabled and an explicit warning. The candidate checks actual requested
MaxLobbyMembers rather than guessing from current occupancy. It never silently
reduces the configured player limit. The user has not chosen a production limit
change; all tests remain private and production remains unchanged.

A guarded private 15-human test successfully requested 16 identities with RTC
and ManualJoin, received a 16-member lobby, resolved its room name, and reported
the host connected. Evidence: [room allocation and connection](evidence/voice-room-probe.json)
and [backend limit](evidence/voice-limit-probe.json). Microphone capture, two
vanilla clients hearing one another, spatial falloff, mute/reconnect behavior,
and simultaneous voice capacity remain unverified. mediaVerified remains false.

Pinned native source: networking.cs:4420/4421 enables RTC and manual joining;
:4450 retries the same options. game.cs:79110 returns when room lookup fails;
:79279 joins the room without requiring a host avatar. Epic's
[lobby reference](https://dev.epicgames.com/docs/epic-online-services/multiplayer/lobbies-and-sessions/lobby-interface/lobby-interface-reference)
describes 64 lobby identities; that is not a promise of 64 voice participants.
Its [RTC join API](https://dev.epicgames.com/docs/api-ref/functions/eos-lobby-join-rtc-room)
describes separate room enrollment. The native backend response above provides
the numeric conference limit that documentation research did not establish.

Host, CLI and diagnostic probe builds pass with zero warnings/errors; supervisor
suite reports 42 passed, 0 failed. Test-only observer probes are excluded from the
release package. See tests/VoiceRoomProbe/README.md for guards and reproduction.

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
That position check predates 0.2.0.3, which replaces individual saved-position
spawning with the longest-connected human as the ordinary spawn reference.

Keyboard taps could operate menus and chat, but the available UI automation API
does not expose held movement keys. Driving, picking up items, attaching parts,
and applying paint/wash strokes have not been exercised by the operator. The
client and test host were initially left running for Keagan's gameplay handoff.
Keagan deferred those checks. At 19:17 UTC on November 12, a final verified backup
completed, graceful shutdown saved successfully, and process inspection confirmed
the isolated host had exited. The normal Steam client was left open. The remaining
checks below are still untested. No production server was deployed.

### Remaining acceptance checks

- Live spawn-reference handover, returning-player placement, and simultaneous joins
  under the 0.2.0.3 connection-order policy.
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

## Windows status replacement (0.3.3.7)

On November 14, all 44 supervisor checks passed. New Windows regressions hold
a destination open without delete sharing, verify recovery after release, and
verify bounded failure preserves both the old JSON and flushed replacement.
Atomic replacement retries only Windows access/sharing/lock violations for up to
two seconds. Permanent failures still propagate; the denying reader in the live
incident has not been identified. No destination deletion fallback is used.

## Terrain worker retirement candidate

The host now gates native MapMagic grid updates and refreshes until background
generation and queued/active terrain apply work have finished. Deferred frames
still pump apply work, avoiding a wait cycle while leaving EOS and other Unity
updates running. Default terrain concurrency is four background workers,
configurable with BepInEx `Performance.TerrainWorkerLimit`. Both MapMagic and
Den.Tools assembly hashes are verified before patch installation.

The guard operates at the complete grid-update boundary so dictionary keys,
terrain coordinates, and transforms remain consistent. It never drops individual
Move calls or blocks the game thread waiting for a worker. Startup-only native
tile-settings changes and arbitrary third-party direct tile mutation are not
covered; runtime changes through those paths need separate lifecycle review.

Source review caught and corrected an active-only apply case: native
`IsQueueEmpty` excludes a currently yielding routine. The guard uses `IsWorking`
under the native enqueue lock. Native regression checks are in
`tests/TerrainGuardProbe`. The native lifecycle suite passed 12 checks, including
paused workers and an active-only apply routine. The final stress run passed
15 checks: both distant destinations reached final applied terrain, 27 tiles
were loaded, and no MapMagic exceptions occurred over 60.012 seconds. It recorded
3,518 frames (58.6 FPS), maximum frame interval 0.204233 seconds, and 3,240
admitted grid updates. All private hosts stopped through native saved shutdown.
The supervisor regression suite passed all 44 checks.

An earlier stress fixture appended observers only in LateUpdate, allowing
FollowPlayers to remove them before native Update sampled the array. Its first
timing-only pass was invalidated when a stronger check found neither destination
had final terrain. The corrected probe pins stable demand immediately before
MapMagic.Update and asserts each destination's generated/applied state. Retain
the earlier failure as fixture evidence, not successful workload coverage.

Private evidence: E:/Odin/scratch/nomad-drive/join-stall-fixes-20261116/guard-case/.
These are three synthetic regions with zero remote clients, not a capacity
benchmark. Remote multi-player acceptance, 680 km reconnects and sustained public
performance remain unverified. No public runtime or world data was changed.

## 0.3.5.9 collider grid stall

Two production snapshots from the same frozen process identified the game
thread in ColliderLodSpatialGrid.QueryRing, called by ColliderLODManager.LateUpdate.
The native method scans every cell in a square whose size grows with the
movement-derived radius. The host patch keeps native small queries and checks
existing buckets for large queries, keeping the native candidate boundaries
and downstream collider decisions. Oversized radii consider all existing cells.

Seven isolated game checks passed on game 1.0.1, Steam build 26391437, including
patch installation, signed coordinates, boundary membership, coordinate wrap,
and finite oversized and infinite radii. The private world saved and stopped.
The supervisor suite passed 44 checks. Sustained remote play and the specific
source of the captured radius have not been verified.
Evidence is in docs/evidence/collider-grid-20261118.json.

## Unexpected host network shutdown

On November 18, 2026, two private lobbies running Nomad Drive 1.0.1 (Steam build 26391437)
completed a backup and then ran Mirror's `StopHost()`. Each supervisor verified the native
checkpoint, terminated its offline child, and started the same slot in a new process.
The checkpoint and backup SHA-256 values matched their pre-failure values when the
replacement reached ready. Both replacement hosts completed a save and graceful stop.

In the second run an exclusive Windows handle on `meta.json` blocked verification.
The supervisor kept the original process alive through two sharing violations and
recovered after the handle closed. The 45 supervisor checks passed, including refusal
of wrong-slot, truncated, and same-size corrupt checkpoint data. These runs used zero
remote players. See `tests/RuntimeHealthProbe/README.md` for the native test procedure.
## Windows control panel

The local Windows candidate was tested against Nomad Drive 1.0.1, Steam build 26391437. A private native host passed 17 operations checks, including start, attachment without a duplicate process, an acknowledged announcement, save, a CRC-verified backup, restart into the same slot and graceful stop. A synthetic authenticated remote connection exercised name resolution, Unicode display, local-host exclusion and removal on disconnect.

The desktop fixtures exercised window close and reactivation, settings sections, masked passwords, validated config writes, stale status, plugin package installation and toggling, profile persistence and sign-in registration using a disposable registry key. Manual Windows sign-in after reboot and a real remote player's on-screen announcement remain unverified.
