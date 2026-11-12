# Command-line configuration

The server requires a working graphics device and driver even though administration is command-line only. Keep `headless` set to `false`. Nomad Drive reads vehicle paint and restoration data back from GPU textures while saving; `-nographics` cannot preserve those changes. The supervisor uses batch mode with a small hidden graphics surface. A Windows server session must still provide a usable graphics context.

Build the supervisor with the .NET 9 SDK:

```powershell
dotnet build src/Supervisor/Supervisor.csproj -c Release
dotnet run --project tests/Supervisor.Tests/Supervisor.Tests.csproj
```

The executable is `src/Supervisor/bin/Release/net9.0/NomadDrive.Server.exe`. These examples assume it is on your PATH. The server requires a separately prepared game runtime and hosting plugin; compiling the supervisor alone does not install them.

```powershell
NomadDrive.Server init --config C:/Servers/Nomad/server.json
NomadDrive.Server validate --config C:/Servers/Nomad/server.json
NomadDrive.Server run --config C:/Servers/Nomad/server.json
```

`init` creates a complete config and refuses to overwrite an existing file. Edit it before `run`. The config is strict JSON, with case-sensitive property names; unknown properties are rejected. Relative paths resolve against the config file directory, not the current terminal directory. The running supervisor takes a configuration snapshot for each game launch. Restart the supervisor to apply changed configuration.

## Startup settings

| Setting | Default | Meaning |
| --- | --- | --- |
| `schemaVersion` | `1` | Configuration format version. |
| `gameDirectory` | `./runtime` | Separate game installation containing the prepared-runtime marker `.nomaddrive-server-runtime`. |
| `dataDirectory` | `./data` | Server-owned saves, backups, logs, and IPC state. Must not overlap the game directory. |
| `headless` | `false` | Must remain `false`. Launches with `-batchmode` and a hidden 640 by 480 graphics surface. `true` is rejected because no-graphics mode cannot preserve vehicle paint/restoration saves. |
| `lobby.name` | `Nomad Drive Server` | Lobby name, 1 to 24 characters. |
| `lobby.maxPlayers` | `4` | Human player slots, 1 to 63. The host identity consumes another lobby membership. The upper bound is a protocol limit, not a tested performance guarantee. |
| `lobby.public` | `false` | Whether the lobby is publicly listed. |
| `lobby.password` | empty | Optional password, up to 24 characters. |
| `world.name` | `Server World` | New world name, 1 to 24 characters. |
| `world.seed` | `0` | New world seed, 0 to 999999. |
| `world.preset` | `Normal` | `Cozy`, `Normal`, `Nomad`, or `Custom`. |
| `world.foodDensity` | `100` | Custom food density, 25 to 200 in increments of 5. |
| `world.waterDensity` | `100` | Custom water density, same range. |
| `world.fuelDensity` | `100` | Custom fuel density, same range. |
| `world.partsDensity` | `100` | Custom parts density, same range. |
| `world.medicineDensity` | `100` | Custom medicine density, same range. |

World settings apply when creating a world. Subsequent starts resume the slot recorded in `dataDirectory/world.json`. Importing a world preserves that world's settings. A missing selected slot is an error; the supervisor never silently replaces it with a new world. The game validates imported save format during startup.

## Public lobbies

Set `lobby.public` to `true` to advertise through the game's public lobby browser.
For an open lobby, leave `lobby.password` empty:

```json
"lobby": {
  "name": "Nomad Drive Server",
  "maxPlayers": 4,
  "public": true,
  "password": ""
}
```

Replace the existing `lobby` section with this example, then restart the supervisor.
The vanilla browser searches for public lobbies with the same game version.
Visibility and password are independent: a public lobby can also have a password.
The host identity can appear as one occupied slot even when no humans are connected.
Public browser discovery, an unmodified Steam client joining, visible server chat,
and rejoining after a saved restart have passed live tests. Vehicle and inventory
persistence checks remain open. See [validation](VALIDATION.md).

## Backups, restarts, and recovery

| Setting | Default | Meaning |
| --- | --- | --- |
| `backups.enabled` | `true` | Enable scheduled save-and-backup operations. Manual `backup` remains available when disabled. |
| `backups.messagesEnabled` | `false` | Broadcast backup start and completion notices. |
| `backups.startMessage` | `Server backup starting.` | Text sent before a backup starts. |
| `backups.completedMessage` | `Server backup completed.` | Text sent after a verified backup completes. |
| `backups.intervalMinutes` | `30` | Time between scheduled backups, 1 to 525600 minutes. |
| `backups.keep` | `48` | Number of this tool's completed ZIP archives to retain, 1 to 100000. |
| `restarts.enabled` | `true` | Enable periodic restarts. Manual `restart` remains available when disabled. |
| `restarts.intervalHours` | `6` | Interval since a ready server session starts, 1 to 8760 hours. |
| `restarts.warningSeconds` | `[600,300,60,10]` | Warning thresholds before a scheduled restart. Each is unique, positive, and below the restart interval. An empty array disables countdown notices. |
| `restarts.messagesEnabled` | `true` | Broadcast countdown and stop/restart notices. |
| `restarts.warningMessage` | `Server restart in {remaining}.` | Countdown text. Optional `{remaining}` expands to a duration such as `5 minutes`, `1 minute`, or `10 seconds`. |
| `restarts.restartingMessage` | `Server is saving and restarting now.` | Text sent before restart saves and shuts down. |
| `restarts.stoppingMessage` | `Server is saving and stopping now.` | Text sent before a requested stop saves and shuts down. |
| `recovery.enabled` | `true` | Restart a crashed game process or failed startup within the retry budget. |
| `recovery.maxAttempts` | `3` | Recovery launches allowed per rolling window, 0 to 100. |
| `recovery.windowMinutes` | `30` | Rolling retry window, 1 to 10080 minutes. Brief successful sessions do not reset this budget. |
| `recovery.delaySeconds` | `15` | Delay before a recovery attempt, 1 to 3600 seconds. |
| `startupTimeoutSeconds` | `300` | Time allowed for the hosting plugin to report ready, 10 to 3600 seconds. |
| `commandTimeoutSeconds` | `120` | Time allowed for a plugin command, 10 to 3600 seconds. |
| `shutdownTimeoutSeconds` | `30` | Time allowed for process exit after a successful shutdown acknowledgement, 5 to 600 seconds. |

The plugin completes a save before copying an immutable snapshot. The supervisor archives that snapshot, compares the archived bytes with the snapshot using SHA-256, publishes the ZIP, applies retention, and removes that successfully archived snapshot. Failed snapshots and incomplete `.partial` archives are retained for diagnosis. Existing archives are not pruned after a failed save or failed archive. Retention only targets files matching the supervisor's exact archive naming pattern; unrelated files are left alone.

Message text must contain 1 to 256 characters without control characters. Use `messagesEnabled: false` to disable notices rather than blank text. The warning template supports only `{remaining}`; omit it for a fixed warning. Unknown or unbalanced placeholders are rejected. Rendered warnings must also fit within 256 characters at and below every configured warning threshold. If a long operation delays a warning, it reports the actual time remaining rounded up to a second, and crossed thresholds produce only one warning. Other message fields are literal text.

A scheduled restart saves and waits for game exit before launching the same world. A failed save cancels that restart. A timed-out command might still be running, so the supervisor stops issuing additional save/shutdown commands and reports `attention`. It does not force-terminate a ready session. Failed or timed-out startup can be terminated because no ready session was established. A surviving process from a previous supervisor blocks another launch; inspect it before taking manual action. Each data directory and each game runtime also has an exclusive supervisor lock.

A plugin error, a missing status file, or a heartbeat older than `commandTimeoutSeconds` changes the supervisor status to `attention` and suspends scheduled operations. This does not kill the game. Manual commands remain available so the plugin can attempt a graceful stop; command timeouts still apply. `status` reads the current plugin status even while the supervisor is waiting for a long operation.

Recovery resumes the last completed save. A crash can lose changes since that save. Disabling scheduled backups disables this supervisor's periodic save trigger; it does not disable manual save, shutdown save, or any independent game/plugin autosave policy.

## Operations

Run administrative commands in another terminal using the same config:

```powershell
NomadDrive.Server status --config C:/Servers/Nomad/server.json
NomadDrive.Server say --config C:/Servers/Nomad/server.json --text "Restart planned later tonight."
NomadDrive.Server save --config C:/Servers/Nomad/server.json
NomadDrive.Server backup --config C:/Servers/Nomad/server.json
NomadDrive.Server restart --config C:/Servers/Nomad/server.json
NomadDrive.Server stop --config C:/Servers/Nomad/server.json
```

`say` permits 1 to 256 characters with no control characters. `restart` is immediate after its save; countdown warnings belong to the scheduled restart policy. Ctrl+C in the supervisor console requests a graceful save and stop. A failed stop leaves the server running and reports why. Commands and saves are serialized. Exit code `0` means success; `1` means failure.

Import a copy of an existing slot before the first server launch, into a data directory that does not already have a selected world:

```powershell
NomadDrive.Server import --config C:/Servers/Nomad/server.json --source "C:/Path/To/Existing/slot_example"
```

Use a completed slot directory containing `save.dat` or `autosave.dat`, not the entire saves root. Stop the source game before copying to avoid taking a mixed snapshot. Import copies every file under that slot into a new server-owned slot. It does not edit the source. The tool rejects reparse points and does not overwrite an already selected server world.

Logs live under `dataDirectory/logs` and `dataDirectory/runs/<launch-id>/player.log`. `supervisor.json` records the current supervisor and game status. Every launch has its own request/response directory, preventing replay of commands from an older game session. These files and startup snapshots may contain the lobby password or join code; keep the server data directory private. This CLI exposes no remote administration port.

The hosting plugin disables the game's competing autosave and cloud-save jobs.
Scheduled backups perform a save first; stop and restart also save. If you disable
both scheduled backups and restarts, use `save` or `backup` for periodic saves.
Stopping the process forcibly cannot save changes since the last completed save.

Only one active server per Windows user/device identity is supported. Separate
runtime directories do not provide separate EOS device identities. Use a separate
Windows profile or machine for another host; simultaneous identity isolation has
not been tested.
