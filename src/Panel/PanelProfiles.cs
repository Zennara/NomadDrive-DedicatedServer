using System.Text.Json;
using Microsoft.Win32;
using NomadDrive.Server;

namespace NomadDrive.Panel;

public sealed record PanelProfile(string Name, string ConfigPath, bool AutoStart = false)
{
    public override string ToString() => Name;
}

public sealed class PanelProfiles
{
    private sealed record Settings(int Version, bool StartPanelAtSignIn, PanelProfile[] Profiles);
    private readonly string path;
    private readonly Action<bool> registerAtSignIn;
    private Settings settings;

    internal PanelProfiles(string settingsDirectory, Action<bool> registerAtSignIn, bool interactive = false)
    {
        path = Path.Combine(settingsDirectory, "profiles.json");
        this.registerAtSignIn = registerAtSignIn;
        settings = Read();
        if (interactive && NeedsStartup(settings))
        {
            try
            {
                using var fileLock = AcquireSettingsLock();
                settings = Read();
                if (NeedsStartup(settings)) registerAtSignIn(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or System.Security.SecurityException or InvalidOperationException)
            {
                StartupWarning = "Windows sign-in registration could not be updated. " + ex.Message;
            }
        }
    }

    public IReadOnlyList<PanelProfile> Profiles => Array.AsReadOnly(settings.Profiles);
    public bool StartPanelAtSignIn => settings.StartPanelAtSignIn;
    public bool IsFirstRun => !File.Exists(path);
    public string? StartupWarning { get; }

    public static PanelProfiles Load(bool interactive = true) => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NomadDrive", "DedicatedServerPanel"), RegisterAtSignIn, interactive);

    public static string? DefaultConfigPath
    {
        get
        {
            var bin = AppContext.BaseDirectory;
            return new[] { Path.Combine(bin, "server.json"), Path.GetFullPath(Path.Combine(bin, "..", "server.json")) }
                .FirstOrDefault(File.Exists);
        }
    }

    public PanelProfile Add(string name, string configPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        var fullPath = Path.GetFullPath(configPath);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("Select an existing server configuration.", fullPath);
        var profile = new PanelProfile(name.Trim(), fullPath);
        Change(current =>
        {
            if (current.Profiles.Any(p => SamePath(p.ConfigPath, fullPath)))
                throw new InvalidOperationException("This configuration already has a profile.");
            return current with { Profiles = [.. current.Profiles, profile] };
        });
        return profile;
    }

    public void Remove(string configPath) => Change(current =>
    {
        var profile = Find(current, configPath);
        return current with { Profiles = current.Profiles.Where(p => p != profile).ToArray() };
    });

    public void SetAutoStart(string configPath, bool enabled) => Change(current =>
    {
        var profile = Find(current, configPath);
        return current with { Profiles = current.Profiles.Select(p => p == profile ? p with { AutoStart = enabled } : p).ToArray() };
    });

    public void SetPanelStartup(bool enabled) =>
        Change(current => current with { StartPanelAtSignIn = enabled });

    private static bool SamePath(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static PanelProfile Find(Settings current, string configPath) =>
        current.Profiles.SingleOrDefault(p => SamePath(p.ConfigPath, Path.GetFullPath(configPath)))
        ?? throw new InvalidOperationException("This profile no longer exists.");

    private Settings Read()
    {
        if (!File.Exists(path)) return new Settings(1, false, []);
        var data = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), ServerConfig.Json);
        if (data is not { Version: 1, Profiles: not null } ||
            data.Profiles.Any(p => p == null || string.IsNullOrWhiteSpace(p.Name) ||
                string.IsNullOrWhiteSpace(p.ConfigPath) || !Path.IsPathFullyQualified(p.ConfigPath)) ||
            data.Profiles.Select(p => p.ConfigPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != data.Profiles.Length)
            throw new InvalidDataException("The saved panel profile list is invalid.");
        return data;
    }

    private static bool NeedsStartup(Settings data) => data.StartPanelAtSignIn || data.Profiles.Any(p => p.AutoStart);

    private FileStream AcquireSettingsLock()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private void Change(Func<Settings, Settings> edit)
    {
        using var fileLock = AcquireSettingsLock();
        var previous = Read();
        var updated = edit(previous);
        SafeFiles.AtomicJson(path, updated);
        try
        {
            if (NeedsStartup(previous) || NeedsStartup(updated))
                registerAtSignIn(NeedsStartup(updated));
        }
        catch
        {
            SafeFiles.AtomicJson(path, previous);
            throw;
        }
        settings = updated;
    }

    private static void RegisterAtSignIn(bool enabled)
        => RegisterAtSignIn(enabled, @"Software\Microsoft\Windows\CurrentVersion\Run",
            Path.Combine(AppContext.BaseDirectory, "NomadDrive.Panel.exe"));

    private static bool IsPanelCommand(object value, string executable)
    {
        const string suffix = "\" --startup";
        if (value is not string command || command.Length <= suffix.Length + 1 ||
            command[0] != '"' || !command.EndsWith(suffix, StringComparison.Ordinal)) return false;
        var previousPath = command[1..^suffix.Length];
        return Path.IsPathFullyQualified(previousPath) && !previousPath.Contains('"') &&
            SamePath(Path.GetFileName(previousPath), Path.GetFileName(executable));
    }

    internal static void RegisterAtSignIn(bool enabled, string runKey, string executable)
    {
        const string valueName = "NomadDriveDedicatedServerPanel";
        var command = $"\"{executable}\" --startup";
        using var current = Registry.CurrentUser.OpenSubKey(runKey);
        var existing = current?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (existing != null && !IsPanelCommand(existing, executable))
        {
            if (!enabled) return;
            throw new InvalidOperationException($"Windows sign-in value {valueName} belongs to another command.");
        }
        if (!enabled)
        {
            if (existing == null) return;
            using var key = Registry.CurrentUser.OpenSubKey(runKey, writable: true);
            key?.DeleteValue(valueName, throwOnMissingValue: false);
            return;
        }
        if (!File.Exists(executable)) throw new FileNotFoundException("The installed Windows panel could not be found.", executable);
        if (command.Length > 260) throw new IOException("Windows sign-in commands are limited to 260 characters. Move the panel to a shorter path.");
        if (existing is string configured && SamePath(configured, command) &&
            current!.GetValueKind(valueName) == RegistryValueKind.String) return;
        using var run = Registry.CurrentUser.CreateSubKey(runKey, writable: true);
        run.SetValue(valueName, command, RegistryValueKind.String);
    }
}
