# Windows setup

This tool runs an isolated copy of the full game with a BepInEx hosting plugin. It does not include the game, game assets, credentials, or Unity libraries. You need your own legitimate Nomad Drive installation. Nothing is published to GitHub or installed into your normal game by these scripts.

Management is command-line only, and the game runs in a hidden batch process. A working graphics context is required: the game's paint and restoration systems use GPU operations on the server. `-nographics` is not supported, and `headless: true` is rejected. This is an unattended host, not a graphics-free dedicated-server executable.

The adapter currently accepts Steam build **26391437**, Unity **6000.0.73f1**, with this `Assembly-CSharp.dll` SHA-256:

```text
6957F9A1180D811CA2AE1BB44748157D794FED5BEFD845290FD15E41FB54FFAF
```

An updated game requires reviewing and rebuilding the adapter. Setup and the plugin refuse an unknown assembly instead of applying old patches to it. Automatic game and plugin updates are not performed.

## Prerequisites

- Windows x64 and Windows PowerShell 5.1 or later.
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) on `PATH`. The published CLI requires the .NET 9 runtime, which the SDK includes.
- Windows `tar.exe` on `PATH` (included with current Windows versions).
- A clean, supported game installation. Existing BepInEx/MelonLoader directories and Doorstop loader files are rejected as a source, so installed third-party mods are not silently copied.
- Space for a second game copy and the 4.5 GB Unity editor archive used to obtain its unstripped Windows Mono libraries. Setup extracts those libraries without installing or launching the Unity editor.

Run from the repository root:

```powershell
.\scripts\setup-runtime.ps1 `
  -GameDirectory 'C:/Program Files (x86)/Steam/steamapps/common/Nomad Drive' `
  -RuntimeDirectory 'C:/Servers/NomadDrive/runtime' `
  -DownloadDirectory 'E:/Odin/scratch/nomad-drive/setup/downloads'

.\scripts\build.ps1 -RuntimeDirectory 'C:/Servers/NomadDrive/runtime'
```

`RuntimeDirectory` must be new or empty. Source, runtime, and download directories must be separate and cannot contain one another. Junctions and symbolic links are rejected. `-ValidateOnly` checks paths, the game version, and prerequisites without downloading or copying files. If setup fails after copying begins, it leaves the partial directory for inspection and does not mark it ready. Use a different empty destination after resolving the failure.

Setup downloads BepInEx **5.4.23.6** from its official GitHub release and the exact Unity **6000.0.73f1** editor archive from Unity. Both downloads are SHA-256 pinned and cached. The archive is packaged for Linux but contains Mono libraries for every player platform; setup extracts only `unityjit-win32`, the Windows profile. It never executes an editor or installer. The generic `unity.bepinex.dev/corlibs` archive is unsuitable here: modern Mono profiles are platform-specific, and that archive contains Unix libraries that fail on this Windows player with `DllNotFoundException: System.Native`. See the [upstream issue](https://github.com/BepInEx/UnityDataMiner/issues/3).

The source game is read-only during setup. The isolated runtime receives BepInEx, the Windows Mono profile, and a `.nomaddrive-server-runtime` marker. Building stages the CLI in `artifacts/cli` and copies only this project's plugin DLL into `BepInEx/plugins/DedicatedServer`. Building refuses to replace the plugin while that runtime's game process is running.

## Configure and run

```powershell
.\artifacts\cli\NomadDrive.Server.exe init --config .\server.json
```

Edit `server.json`, setting `gameDirectory` to your isolated runtime and `dataDirectory` to a separate persistent server-data directory. See [CONFIG.md](CONFIG.md) for startup settings, schedules, and retention.

```powershell
.\artifacts\cli\NomadDrive.Server.exe validate --config .\server.json
.\artifacts\cli\NomadDrive.Server.exe run --config .\server.json
```

The supervisor stays in the foreground. Use a second terminal for management:

```powershell
.\artifacts\cli\NomadDrive.Server.exe status --config .\server.json
.\artifacts\cli\NomadDrive.Server.exe say --config .\server.json --text 'Server maintenance shortly.'
.\artifacts\cli\NomadDrive.Server.exe backup --config .\server.json
.\artifacts\cli\NomadDrive.Server.exe restart --config .\server.json
.\artifacts\cli\NomadDrive.Server.exe stop --config .\server.json
```

Keep server data and the runtime outside your download/scratch directory. The download cache is not your save backup. Setup leaves downloaded archives and extracted libraries in the selected download directory so failures can be diagnosed without fetching them again.

## Import a world

Before the first server run, use an unused server-data directory and copy an existing game save slot through the CLI:

```powershell
.\artifacts\cli\NomadDrive.Server.exe import --config .\server.json --source 'C:/Path/To/EvilSave/slot_example'
```

This imports a copy. Keep the original slot as a backup and make sure the source game is not writing it during import. Existing server worlds are not overwritten. The slot identifier in `dataDirectory/world.json` determines which world resumes after restarts. Imported worlds keep their saved seed and economy settings.

## Additional plugins

Place BepInEx 5 plugins and their declared dependencies in the isolated runtime's `BepInEx/plugins`, following the plugin author's instructions. Stop the server before changing them. No automatic plugin downloads, updates, or compatibility conversions are performed.

BepInEx support does not make every mod compatible with vanilla clients. Plugins that change only server behavior through existing game messages may work; new message types, networked prefabs, assets, or client UI can require matching client mods. Plugins that change saves, lobby limits, process shutdown, or world generation can conflict with the hosting adapter. Test each plugin against a copied world before using it on your retained server world.
