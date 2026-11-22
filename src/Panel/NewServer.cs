using System.Diagnostics;
using System.Text.Json;
using NomadDrive.Server;

namespace NomadDrive.Panel;

internal static class NewServer
{
    internal static bool HasRuntime(string directory) =>
        File.Exists(Path.Combine(directory, ".nomaddrive-server-runtime")) &&
        File.Exists(Path.Combine(directory, "Nomad Drive.exe")) &&
        File.Exists(Path.Combine(directory, "BepInEx", "plugins", "DedicatedServer", "NomadDrive.DedicatedServer.dll"));

    internal static (string Runtime, string Data) CheckPaths(string path, ServerConfig settings, IEnumerable<PanelProfile> existing)
    {
        settings.Validate();
        path = Path.GetFullPath(path);
        if (File.Exists(path) || Directory.Exists(path)) throw new IOException("Choose a new config filename.");
        var root = Path.GetDirectoryName(path)!;
        var runtime = Path.GetFullPath(settings.GameDirectory, root);
        var data = Path.GetFullPath(settings.DataDirectory, root);
        SafeFiles.RejectReparseAncestors(path);
        SafeFiles.RejectReparseAncestors(runtime);
        SafeFiles.RejectReparseAncestors(data);
        if (SafeFiles.Overlaps(runtime, data) || SafeFiles.Overlaps(path, runtime) || SafeFiles.Overlaps(path, data))
            throw new IOException("Keep the config, runtime and data in separate locations.");
        if (Directory.Exists(data) && Directory.EnumerateFileSystemEntries(data).Any())
            throw new IOException("New servers need an empty data directory. Use Open config to add an existing server.");
        foreach (var profile in existing)
        {
            var config = ServerConfig.Load(profile.ConfigPath);
            if (new[] { config.GameDirectory, config.DataDirectory }.Any(other => SafeFiles.Overlaps(other, runtime) || SafeFiles.Overlaps(other, data)))
                throw new IOException($"Choose separate runtime and data directories from {profile.Name}.");
        }
        RuntimeGuard.RejectLiveGame(runtime);
        return (runtime, data);
    }

    internal static async Task PrepareRuntimeAsync(string cleanGame, string runtime, string? downloadCache = null)
    {
        var setup = Path.Combine(AppContext.BaseDirectory, "runtime-setup");
        var script = Path.Combine(setup, "setup-runtime.ps1");
        var host = Path.Combine(setup, "NomadDrive.DedicatedServer.dll");
        if (!File.Exists(script) || !File.Exists(host))
            throw new IOException("The installed build is missing its runtime setup files. Select an already prepared runtime or reinstall the complete build.");
        var cache = downloadCache ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NomadDrive", "RuntimeDownloads");
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
            "-GameDirectory", cleanGame, "-RuntimeDirectory", runtime, "-DownloadDirectory", cache })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Could not start runtime setup.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(output, error, process.WaitForExitAsync()).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var details = (await error).Trim();
            throw new IOException("Runtime setup failed. " + (details.Length == 0 ? (await output).Trim() : details));
        }

        var plugins = Path.Combine(runtime, "BepInEx", "plugins", "DedicatedServer");
        SafeFiles.RejectReparseAncestors(plugins);
        Directory.CreateDirectory(plugins);
        File.Copy(host, Path.Combine(plugins, "NomadDrive.DedicatedServer.dll"));
    }

    internal static PanelProfile CreateProfile(string path, ServerConfig config, PanelProfiles profiles)
    {
        var (runtime, _) = CheckPaths(path, config, profiles.Profiles);
        if (!HasRuntime(runtime)) throw new IOException("A prepared game runtime with the hosting plugin is required.");
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            JsonSerializer.Serialize(file, config, ServerConfig.Json);
        return profiles.Add(config.Lobby.Name, path);
    }
}
