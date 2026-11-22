using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using NomadDrive.Server;

namespace NomadDrive.Panel;

internal sealed record PanelStatus(string ServerName, bool Running, string State, string Detail, string LobbyCode,
    int Players, int MaxPlayers, IReadOnlyList<string> Names, bool PlayerCountKnown);

internal sealed record PanelMod(string RelativePath, bool Enabled, bool Protected);

/// <summary>All server commands go to the existing supervisor. The panel does not own the game process.</summary>
internal sealed class PanelBackend(string configPath, string? supervisorPath = null)
{
    private readonly string configFile = Path.GetFullPath(configPath);
    private readonly string executable = Path.GetFullPath(supervisorPath ?? Path.Combine(AppContext.BaseDirectory, "NomadDrive.Server.exe"));

    public Task<PanelStatus> StatusAsync() => Task.Run(() => ReadStatus(ServerConfig.Load(configFile)));

    public async Task StopIfRunningAsync()
    {
        if (!(await StatusAsync().ConfigureAwait(false)).Running) return;
        try { await CommandAsync("stop").ConfigureAwait(false); }
        catch (IOException)
        {
            // A second profile can point at the same supervisor, or it can stop
            // independently between the status check and the command.
            if (!(await StatusAsync().ConfigureAwait(false)).Running) return;
            throw;
        }
        var deadline = Stopwatch.StartNew();
        while ((await StatusAsync().ConfigureAwait(false)).Running)
        {
            if (deadline.Elapsed >= TimeSpan.FromSeconds(10))
                throw new IOException("The supervisor acknowledged shutdown but is still running.");
            await Task.Delay(200).ConfigureAwait(false);
        }
    }

    private static PanelStatus ReadStatus(ServerConfig config)
    {
        var status = Protocol.Read<SupervisorStatus>(Path.Combine(config.DataDirectory, "supervisor.json"));
        if (status is null || !IsAlive(status))
        {
            string detail = status?.State == "error" ? status.Detail : "Server is stopped.";
            try
            {
                RuntimeGuard.RejectLiveGame(config.GameDirectory);
                RuntimeGuard.RejectLiveGame(config.DataDirectory);
            }
            catch (IOException ex)
            {
                return new(config.Lobby.Name, true, "attention", ex.Message, "", 0, config.Lobby.MaxPlayers, [], false);
            }
            return new(config.Lobby.Name, false, "stopped", detail, "", 0, config.Lobby.MaxPlayers, [], false);
        }

        var game = !string.IsNullOrWhiteSpace(status.RunDirectory) && SafeFiles.Inside(status.RunDirectory, Path.Combine(config.DataDirectory, "runs"))
            ? Protocol.Read<PluginStatus>(Path.Combine(status.RunDirectory, "status.json"), tolerateSharingViolation: true) : null;
        var state = status.State;
        var detailText = status.Detail;
        if (state is "attention" or "ready" &&
            HostHealth.Problem(game, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(config.CommandTimeoutSeconds)) is { } problem)
        {
            state = "attention";
            detailText = problem;
        }
        // During recovery the last run directory can still contain a complete old roster.
        if (state is "recovering" or "stopped" or "error" || game?.State != "ready" ||
            DateTimeOffset.UtcNow - game.UpdatedUtc > TimeSpan.FromSeconds(config.CommandTimeoutSeconds))
            game = null;
        var names = game?.Roster?.Select(p => string.IsNullOrWhiteSpace(p.DisplayName)
            ? $"Player #{p.ConnectionId}" : p.DisplayName).ToArray() ?? [];
        return new(config.Lobby.Name, true, state, detailText, game?.LobbyCode ?? "", game?.Players ?? 0,
            game?.MaxPlayers ?? config.Lobby.MaxPlayers, names, game is not null);
    }

    private static bool IsAlive(SupervisorStatus status)
    {
        try
        {
            using var process = Process.GetProcessById(status.SupervisorPid);
            return !process.HasExited && Math.Abs((process.StartTime.ToUniversalTime() - status.SupervisorStartedUtc).TotalSeconds) < 2;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    public async Task<string> StartAsync()
    {
        var config = ServerConfig.Load(configFile, requireRuntime: true);
        var current = ReadStatus(config);
        if (current.Running) return current.Detail;
        using var process = Process.Start(Invocation("run")) ?? throw new IOException("Could not launch the supervisor.");
        var timer = Stopwatch.StartNew();
        do
        {
            var status = ReadStatus(config);
            if (status.Running) return status.Detail;
            if (process.HasExited)
                throw new IOException($"Supervisor exited during startup (code {process.ExitCode}). {TailLog("Supervisor")}");
            await Task.Delay(150).ConfigureAwait(false);
        } while (timer.Elapsed < TimeSpan.FromSeconds(8));
        return $"Supervisor launched (PID {process.Id}). Waiting for startup status.";
    }

    private ProcessStartInfo Invocation(string command)
    {
        if (!File.Exists(executable)) throw new FileNotFoundException("The panel requires its bundled NomadDrive.Server.exe.", executable);
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetDirectoryName(configFile)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var arg in new[] { command, "--config", configFile }) info.ArgumentList.Add(arg);
        return info;
    }

    public async Task<string> CommandAsync(string command, string? text = null)
    {
        if (command is not ("say" or "save" or "backup" or "restart" or "stop" or "validate" or "import"))
            throw new ArgumentException("Unknown server operation.", nameof(command));
        var info = Invocation(command);
        if (command is "say" or "import")
        {
            if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("This operation requires a value.");
            info.ArgumentList.Add(command == "say" ? "--text" : "--source");
            info.ArgumentList.Add(text);
        }
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        using var process = Process.Start(info) ?? throw new IOException("Could not run the supervisor command.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(output, errors, process.WaitForExitAsync()).ConfigureAwait(false);
        var message = output.Result.Trim();
        if (message.StartsWith('{'))
        {
            var response = JsonSerializer.Deserialize<CommandResponse>(message, Protocol.Json);
            if (response is not null) message = response.Ok ? response.Message : response.Error;
        }
        if (process.ExitCode != 0) throw new IOException(string.IsNullOrEmpty(errors.Result.Trim()) ? message : errors.Result.Trim());
        return string.IsNullOrEmpty(message) ? $"Completed {command}." : message;
    }

    public string TailLog(string kind)
    {
        var config = ServerConfig.Load(configFile);
        string? file;
        switch (kind.ToLowerInvariant())
        {
            case "plugins":
                file = Path.Combine(config.GameDirectory, "BepInEx", "LogOutput.log");
                break;
            case "supervisor":
                var logs = Path.Combine(config.DataDirectory, "logs");
                SafeFiles.RejectReparseAncestors(logs);
                file = Directory.Exists(logs) ? Directory.EnumerateFiles(logs, "supervisor-*.log").Max(StringComparer.OrdinalIgnoreCase) : null;
                break;
            case "game":
                var status = Protocol.Read<SupervisorStatus>(Path.Combine(config.DataDirectory, "supervisor.json"));
                file = status is not null && !string.IsNullOrWhiteSpace(status.RunDirectory) &&
                    SafeFiles.Inside(status.RunDirectory, Path.Combine(config.DataDirectory, "runs"))
                    ? Path.Combine(status.RunDirectory, "player.log") : null;
                break;
            default: throw new ArgumentException("Unknown log.", nameof(kind));
        }
        if (file is null || !File.Exists(file)) return "";
        SafeFiles.RejectReparseAncestors(file);
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = stream.Length;
        var count = (int)Math.Min(length, 64 * 1024);
        var bytes = new byte[count];
        stream.Position = length - count;
        var read = stream.ReadAtLeast(bytes, count, throwOnEndOfStream: false);
        var lines = Encoding.UTF8.GetString(bytes, 0, read).Split('\n');
        return string.Join(Environment.NewLine, lines.Skip(length > count ? 1 : 0).TakeLast(300).Select(line => line.TrimEnd('\r')));
    }

    // Directory units preserve companion DLLs, assets and dependency layout on disable/enable.
    private static string ModsRoot(ServerConfig config, bool enabled) =>
        Path.Combine(config.GameDirectory, "BepInEx", enabled ? "plugins" : "panel-disabled-plugins");

    private static bool Protected(string name) =>
        name.Equals("DedicatedServer", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("NomadDrive.DedicatedServer.dll", StringComparison.OrdinalIgnoreCase);

    private static bool HasHost(string path) => File.Exists(path)
        ? Protected(Path.GetFileName(path))
        : Directory.EnumerateFiles(path, "NomadDrive.DedicatedServer.dll", SearchOption.AllDirectories).Any();

    public IReadOnlyList<PanelMod> Mods()
    {
        var config = ServerConfig.Load(configFile);
        var mods = new List<PanelMod>();
        foreach (var enabled in new[] { true, false })
        {
            var root = ModsRoot(config, enabled);
            SafeFiles.RejectReparseAncestors(root);
            if (!Directory.Exists(root)) continue;
            foreach (var path in Directory.EnumerateFileSystemEntries(root))
            {
                SafeFiles.RejectReparseAncestors(path);
                if (Directory.Exists(path))
                {
                    SafeFiles.CheckTree(path);
                    if (!Directory.EnumerateFiles(path, "*.dll", SearchOption.AllDirectories).Any()) continue;
                }
                else if (!path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                var name = Path.GetFileName(path);
                mods.Add(new(name, enabled, Protected(name) || HasHost(path)));
            }
        }
        return mods.OrderBy(mod => mod.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void CheckPrepared(ServerConfig config)
    {
        config.Validate(requireRuntime: true);
        SafeFiles.RejectReparseAncestors(Path.Combine(config.GameDirectory, "server.lock"));
        SafeFiles.RejectReparseAncestors(Path.Combine(config.DataDirectory, "server.lock"));
    }

    private static void CheckOffline(ServerConfig config)
    {
        RuntimeGuard.RejectLiveGame(config.DataDirectory);
        RuntimeGuard.RejectLiveGame(config.GameDirectory);
    }

    public void SetModEnabled(PanelMod mod, bool enabled)
    {
        var config = ServerConfig.Load(configFile);
        CheckPrepared(config);
        using var dataLock = SafeFiles.AcquireLock(config.DataDirectory);
        using var runtimeLock = SafeFiles.AcquireLock(config.GameDirectory);
        CheckOffline(config);
        if (enabled == mod.Enabled) return;
        SafeName(mod.RelativePath);
        var from = Path.Combine(ModsRoot(config, mod.Enabled), mod.RelativePath);
        var to = Path.Combine(ModsRoot(config, enabled), mod.RelativePath);
        SafeFiles.RejectReparseAncestors(from);
        SafeFiles.RejectReparseAncestors(to);
        if (!File.Exists(from) && !Directory.Exists(from)) throw new IOException("The selected mod has changed. Refresh the mod list.");
        if (Directory.Exists(from)) SafeFiles.CheckTree(from);
        if (Protected(mod.RelativePath) || HasHost(from)) throw new IOException("The dedicated hosting plugin is required.");
        EnsureVacant(to);
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        if (Directory.Exists(from)) Directory.Move(from, to);
        else File.Move(from, to);
    }

    public void InstallMod(string sourceFile)
    {
        sourceFile = Path.GetFullPath(sourceFile);
        SafeFiles.RejectReparseAncestors(sourceFile);
        var config = ServerConfig.Load(configFile);
        CheckPrepared(config);
        using var dataLock = SafeFiles.AcquireLock(config.DataDirectory);
        using var runtimeLock = SafeFiles.AcquireLock(config.GameDirectory);
        CheckOffline(config);
        var loader = Path.Combine(config.GameDirectory, "BepInEx");
        SafeFiles.RejectReparseAncestors(loader);
        if (!Directory.Exists(Path.Combine(loader, "core"))) throw new IOException("BepInEx is missing from the prepared runtime.");
        var staging = Path.Combine(loader, ".panel-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            string name;
            if (sourceFile.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                name = Path.GetFileName(sourceFile);
                SafeName(name);
                _ = AssemblyName.GetAssemblyName(sourceFile);
                File.Copy(sourceFile, Path.Combine(staging, name));
            }
            else if (sourceFile.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                name = ExtractPackage(sourceFile, staging);
            else throw new InvalidDataException("Choose a managed plugin DLL or a BepInEx plugin package ZIP.");

            var unit = Path.Combine(staging, name);
            if (Protected(name) || HasHost(unit)) throw new InvalidDataException("Use the dedicated server installer to update its required hosting plugin.");
            var target = Path.Combine(ModsRoot(config, true), name);
            foreach (var enabled in new[] { true, false })
            {
                var path = Path.Combine(ModsRoot(config, enabled), name);
                SafeFiles.RejectReparseAncestors(path);
                EnsureVacant(path);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (Directory.Exists(unit)) Directory.Move(unit, target);
            else File.Move(unit, target);
        }
        finally
        {
            SafeFiles.CheckTree(staging);
            Directory.Delete(staging, recursive: true);
        }
    }

    private static void EnsureVacant(string path)
    {
        if (File.Exists(path) || Directory.Exists(path)) throw new IOException($"A mod already occupies {path}.");
    }

    private static void SafeName(string component)
    {
        var device = component.Split('.')[0].ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(component) || component is "." or ".." || component.EndsWith('.') || component.EndsWith(' ') ||
            component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            device is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            (device.Length == 4 && (device.StartsWith("COM") || device.StartsWith("LPT")) && "123456789¹²³".Contains(device[3])))
            throw new InvalidDataException("Invalid plugin path component: " + component);
    }

    private static string ExtractPackage(string source, string staging)
    {
        using var zip = ZipFile.OpenRead(source);
        string? unit = null;
        long size = 0;
        var hasDll = false;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            var directory = name.EndsWith('/');
            var parts = name.TrimEnd('/').Split('/');
            foreach (var part in parts) SafeName(part);
            if (!parts[0].Equals("BepInEx", StringComparison.OrdinalIgnoreCase))
            {
                if (IsPackageDocumentation(parts)) continue;
                throw new InvalidDataException("This ZIP includes additional runtime files outside BepInEx/plugins. Install it manually.");
            }
            if (directory && parts.Length < 3) continue;
            if (parts.Length < 3 || !parts[1].Equals("plugins", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Packages with files outside BepInEx/plugins require manual installation.");
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked package entries are unsupported.");
            unit ??= parts[2];
            if (!unit.Equals(parts[2], StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Package must contain one mod folder or a single DLL under BepInEx/plugins.");
            if (directory) continue;
            size = checked(size + entry.Length);
            if (size > 1024L * 1024 * 1024) throw new InvalidDataException("Uncompressed plugin package exceeds 1 GiB.");
            var target = Path.GetFullPath(Path.Combine(staging, Path.Combine(parts[2..])));
            if (!SafeFiles.Inside(target, staging)) throw new InvalidDataException("Invalid plugin destination.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: false);
            hasDll |= target.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        }
        return unit is not null && hasDll ? unit : throw new InvalidDataException("ZIP contains no DLL under BepInEx/plugins.");
    }

    private static bool IsPackageDocumentation(string[] parts)
    {
        var first = parts[0].ToLowerInvariant();
        if (first is "docs" or "evidence" or "examples") return true;
        if (parts.Length != 1) return false;
        return first is "version" or "license" or "manifest.json" or "compatibility.json" or "sha256sums.txt" or "icon.png"
            || first.EndsWith(".md") || first.EndsWith(".txt");
    }
}
