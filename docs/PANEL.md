# Windows server panel

Open `NomadDrive.Panel.exe` in the same folder as `NomadDrive.Server.exe`, or launch it with `NomadDrive.Server.exe panel`. The build stages both in `artifacts/cli`. Install the x64 .NET 9 Desktop Runtime when running the framework-dependent panel on another PC.

Run `scripts/install-panel.ps1 -SourceDirectory artifacts/cli` to install for the current Windows user and create a **Nomad Drive Server Panel** shortcut in the Start menu. Open that shortcut to use the installed copy with existing profiles.

**New server** lets you pick a config location and edit the settings before creating the server. Use a dedicated folder for each server. If its configured runtime is missing, select a clean, supported Nomad Drive installation. The panel runs the bundled runtime setup to make an isolated copy. The first setup downloads the pinned loader and Unity archive, which may take time and disk space. You can also enter the path to an already prepared runtime with the hosting plugin. The new server stays stopped so you can edit settings or install mods before you start it.

**Open config** adds an existing local server. On first launch the panel looks for `server.json` beside the panel executable or one directory above it. Forgetting a profile removes its automatic startup selection and preserves its server files.

| View | Operations |
| --- | --- |
| Server | Start or attach, see live status, lobby code and online players, send chat announcements, save, create a backup, restart, or stop after a save. Older hosts supply a player count only. An unavailable heartbeat is displayed as unknown occupancy. |
| Mods | Inspect the configured runtime's BepInEx plugins. While stopped, install a managed DLL or ZIP containing one plugin under `BepInEx/plugins`, or enable and disable a plugin and its companion files together. The panel protects the hosting plugin. Packages with additional runtime installation steps require manual installation. Existing packages are never overwritten. |
| Settings | Edit startup settings, import a copied save into an unused profile data directory, and set sign-in preferences. World generation settings apply when a new world is created. |
| Logs | Read the most recent supervisor, game or BepInEx log output for the selected profile. |

**Edit config** on the Server view opens the startup settings, even while the server is online. Settings take effect after a full **Stop** and **Start**. Stop the server before changing its data or runtime directory.

Close the window to hide the panel in the system tray. Double-click the icon or run the panel again to reopen the same window. **Shut down all servers** saves and stops all running servers registered in the panel before exiting. If any stop fails, the panel stays open and reports which profiles need attention. Use **Stop** to shut down only the selected server.

Windows sign-in choices are off initially. **Open panel in the tray at sign-in** is in the panel header and applies to the current Windows user. **Start this server at Windows sign-in** is on each server's Settings tab. The panel stays in the tray when it starts a server at sign-in. Opening the panel normally leaves server startup under manual control. Windows must reach that user's sign-in for these settings to take effect.
