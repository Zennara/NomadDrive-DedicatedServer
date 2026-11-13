# NomadDrive-DedicatedServer

Windows command-line supervisor and BepInEx 5 hosting plugin for Nomad Drive.
Runs an isolated copy of the full game with an unattended host and no local
player character. This is not an official dedicated-server executable.

A working Windows graphics device is required. The process runs in the background
with a graphics context because the game's paint and restoration saves depend on
GPU texture readback. No-graphics mode is rejected to prevent losing those changes.

**Preview 0.3.1.5.** Supports descriptive lobby names up to 64 characters, coordinated plugin saves and shutdown preparation for automatic vehicle parking. Compilation and supervisor tests do not establish
vanilla-client compatibility. See [validation](docs/VALIDATION.md) for the actual
runtime results and remaining checks before using a valuable world.

Target game: **Nomad Drive 1.0.1**, Steam build **26391437**. Exact game and loader
compatibility is recorded in [compatibility.json](compatibility.json).
See [release rules](docs/RELEASES.md) for versioning and tagged release policy.

## Setup

Requires a legally installed copy of Nomad Drive, Windows x64, and .NET 9 SDK
to build. The adapter is pinned to Steam build **26391437**, Unity
**6000.0.73f1**. It refuses unknown game assemblies instead of applying
version-specific patches to a changed game.

Follow [installation](docs/INSTALL.md), then [configuration and commands](docs/CONFIG.md).
The setup scripts copy your installed game to a new runtime directory; the
repository does not contain or redistribute the game.

## Features

- Startup JSON for lobby name, visibility, password, human player limit, world
  name, seed, difficulty, and all five resource density controls.
- Public lobbies use `lobby.public: true`; leave `lobby.password` empty for open
  joining. See the [public lobby example](docs/CONFIG.md#public-lobbies).
- Creates a new world or imports a copy of an existing save slot. Restarts select
  the same saved slot. Seed and economy settings from an existing world take
  precedence over new-world configuration.
- Optional backups every 30 minutes, retaining 48 archives by default. The
  plugin awaits a successful save and creates an immutable snapshot; the
  supervisor verifies the ZIP before retiring older archives.
- Optional six-hour restarts, warning players at 10 minutes, 5 minutes,
  1 minute, and 10 seconds. Failed saves cancel shutdown or restart.
- Bounded crash recovery, isolated logs, and command-line administration.
- Standard BepInEx 5 plugin directory for additional host plugins.

## Multiplayer limits

`maxPlayers` counts humans, not the host identity. Configuration supports
**1 through 63** because EOS permits 64 lobby identities and the host consumes
one. Four humans remain the default. The upper bound is a protocol ceiling,
not a performance or stability claim. Economy scaling remains capped at eight
by the game.

For configurations above 15 humans the adapter disables the EOS voice room as
a conservative compatibility policy. This is not a verified 16-person voice
limit. Stock lobby browsers may show one extra occupied slot for the server.

World generation retains the game's host client services and follows connected
players. Floating-origin rebasing follows the first connected player. The game
uses a shared origin, so groups traveling far apart retain the game's precision
limitations.

Lobby visibility and password use the game's existing behavior. Its password
check occurs on clients; it is not hardened server-side access control.

## Additional plugins

Install BepInEx 5 plugins into the prepared runtime's `BepInEx/plugins/` folder
while the server is stopped. Keep this project's plugin under `DedicatedServer`.
No mod manager, automatic plugin updater, or downloaded mod executes as part of
normal startup.

Vanilla clients can use host plugins that operate through existing game messages
and assets. New message types, network components, item types, or assets can
require client plugins. Installing a plugin on the host does not automatically
make its networking vanilla compatible. Plugins that bypass the managed save
gate or change save formats can invalidate backup consistency and import checks.

Source builds reference your installed game and loader libraries without copying
them to published artifacts. The CLI and plugin are separate: the supervisor
uses .NET 9; the plugin targets the game's Mono runtime.
