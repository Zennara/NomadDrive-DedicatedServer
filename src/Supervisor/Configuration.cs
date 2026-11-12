using System.Text.Json;
using System.Text.Json.Serialization;

namespace NomadDrive.Server;

public sealed class ServerConfig
{
    public int SchemaVersion { get; set; } = 1;
    public string GameDirectory { get; set; } = "./runtime";
    public string DataDirectory { get; set; } = "./data";
    public bool Headless { get; set; }
    public LobbyConfig Lobby { get; set; } = new();
    public WorldConfig World { get; set; } = new();
    public BackupConfig Backups { get; set; } = new();
    public RestartConfig Restarts { get; set; } = new();
    public RecoveryConfig Recovery { get; set; } = new();
    public int StartupTimeoutSeconds { get; set; } = 300;
    public int CommandTimeoutSeconds { get; set; } = 120;
    public int ShutdownTimeoutSeconds { get; set; } = 30;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false
    };

    public static ServerConfig Load(string path, bool requireRuntime = false)
    {
        var config = JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("Config must be a JSON object.");
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        config.GameDirectory = Resolve(config.GameDirectory, directory, "gameDirectory");
        config.DataDirectory = Resolve(config.DataDirectory, directory, "dataDirectory");
        config.Validate(requireRuntime);
        return config;
    }

    private static string Resolve(string path, string root, string name)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException($"{name} cannot be empty.");
        return Path.GetFullPath(path, root);
    }

    public void Validate(bool requireRuntime = false)
    {
        if (SchemaVersion != 1) Fail("schemaVersion must be 1.");
        if (Headless) Fail("No-graphics mode cannot preserve vehicle paint/restoration saves. Use headless:false; a hidden graphics context is required.");
        if (Lobby is null || World is null || Backups is null || Restarts is null || Recovery is null)
            Fail("Config sections cannot be null.");
        Text(Lobby!.Name, "lobby.name", 24);
        if (Lobby.Password is null || Lobby.Password.Length > 24) Fail("lobby.password must be a string of at most 24 characters.");
        Range(Lobby.MaxPlayers, 1, 63, "lobby.maxPlayers");
        Text(World!.Name, "world.name", 24);
        Range(World.Seed, 0, 999999, "world.seed");
        if (World.Preset is not ("Cozy" or "Normal" or "Nomad" or "Custom"))
            Fail("world.preset must be Cozy, Normal, Nomad, or Custom.");
        foreach (var (name, value) in new[] { ("foodDensity", World.FoodDensity), ("waterDensity", World.WaterDensity),
                     ("fuelDensity", World.FuelDensity), ("partsDensity", World.PartsDensity), ("medicineDensity", World.MedicineDensity) })
            if (value < 25 || value > 200 || value % 5 != 0) Fail($"world.{name} must be 25..200 in steps of 5.");
        Range(Backups!.IntervalMinutes, 1, 525600, "backups.intervalMinutes");
        Range(Backups.Keep, 1, 100000, "backups.keep");
        ChatMessages.ValidateText(Backups.StartMessage, "backups.startMessage");
        ChatMessages.ValidateText(Backups.CompletedMessage, "backups.completedMessage");
        Range(Restarts!.IntervalHours, 1, 8760, "restarts.intervalHours");
        if (Restarts.WarningSeconds is null || Restarts.WarningSeconds.Length > 100 ||
            Restarts.WarningSeconds.Any(s => s < 1 || s >= Restarts.IntervalHours * 3600) ||
            Restarts.WarningSeconds.Distinct().Count() != Restarts.WarningSeconds.Length)
            Fail("restarts.warningSeconds must contain at most 100 unique positive seconds below the restart interval.");
        ChatMessages.ValidateText(Restarts.RestartingMessage, "restarts.restartingMessage");
        ChatMessages.ValidateText(Restarts.StoppingMessage, "restarts.stoppingMessage");
        ChatMessages.ValidateWarningTemplate(Restarts.WarningMessage);
        foreach (var seconds in Restarts.WarningSeconds!)
        {
            ChatMessages.RenderWarning(Restarts.WarningMessage, seconds);
            // A delayed poll may render seconds instead of exact minutes. The
            // threshold and the preceding second cover the longest text below it.
            if (seconds > 1) ChatMessages.RenderWarning(Restarts.WarningMessage, seconds - 1);
        }
        Range(Recovery!.MaxAttempts, 0, 100, "recovery.maxAttempts");
        Range(Recovery.WindowMinutes, 1, 10080, "recovery.windowMinutes");
        Range(Recovery.DelaySeconds, 1, 3600, "recovery.delaySeconds");
        Range(StartupTimeoutSeconds, 10, 3600, "startupTimeoutSeconds");
        Range(CommandTimeoutSeconds, 10, 3600, "commandTimeoutSeconds");
        Range(ShutdownTimeoutSeconds, 5, 600, "shutdownTimeoutSeconds");
        if (string.IsNullOrWhiteSpace(GameDirectory) || string.IsNullOrWhiteSpace(DataDirectory)) Fail("Both directories are required.");
        if (SafeFiles.Overlaps(GameDirectory, DataDirectory)) Fail("gameDirectory and dataDirectory must be separate, non-nested directories.");
        SafeFiles.RejectReparseAncestors(GameDirectory);
        SafeFiles.RejectReparseAncestors(DataDirectory);
        if (requireRuntime)
        {
            if (!File.Exists(Path.Combine(GameDirectory, ".nomaddrive-server-runtime")))
                Fail("gameDirectory is not a prepared isolated server runtime: .nomaddrive-server-runtime is missing.");
            if (!File.Exists(Path.Combine(GameDirectory, "Nomad Drive.exe"))) Fail("Nomad Drive.exe is missing from gameDirectory.");
        }
    }

    private static void Text(string? value, string name, int max)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > max || value.Any(char.IsControl)) Fail($"{name} must be 1..{max} characters with no control characters.");
    }
    private static void Range(int value, int min, int max, string name)
    {
        if (value < min || value > max) Fail($"{name} must be {min}..{max}.");
    }
    private static void Fail(string message) => throw new InvalidDataException(message);
}

public sealed class LobbyConfig
{
    public string Name { get; set; } = "Nomad Drive Server";
    public int MaxPlayers { get; set; } = 4;
    public bool Public { get; set; }
    public string Password { get; set; } = "";
}
public sealed class WorldConfig
{
    public string Name { get; set; } = "Server World";
    public int Seed { get; set; }
    public string Preset { get; set; } = "Normal";
    public int FoodDensity { get; set; } = 100;
    public int WaterDensity { get; set; } = 100;
    public int FuelDensity { get; set; } = 100;
    public int PartsDensity { get; set; } = 100;
    public int MedicineDensity { get; set; } = 100;
}
public sealed class BackupConfig
{
    public bool Enabled { get; set; } = true;
    public bool MessagesEnabled { get; set; }
    public string StartMessage { get; set; } = "Server backup starting.";
    public string CompletedMessage { get; set; } = "Server backup completed.";
    public int IntervalMinutes { get; set; } = 30;
    public int Keep { get; set; } = 48;
}
public sealed class RestartConfig
{
    public bool Enabled { get; set; } = true;
    public int IntervalHours { get; set; } = 6;
    public int[] WarningSeconds { get; set; } = [600, 300, 60, 10];
    public bool MessagesEnabled { get; set; } = true;
    public string WarningMessage { get; set; } = "Server restart in {remaining}.";
    public string RestartingMessage { get; set; } = "Server is saving and restarting now.";
    public string StoppingMessage { get; set; } = "Server is saving and stopping now.";
}
public sealed class RecoveryConfig
{
    public bool Enabled { get; set; } = true;
    public int MaxAttempts { get; set; } = 3;
    public int WindowMinutes { get; set; } = 30;
    public int DelaySeconds { get; set; } = 15;
}

public static class ChatMessages
{
    public static void ValidateText(string? text, string name)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 256 || text.Any(char.IsControl))
            throw new InvalidDataException(name + " must be 1..256 characters without control characters.");
    }

    public static void ValidateWarningTemplate(string? template)
    {
        ValidateText(template, "restarts.warningMessage");
        var literal = template!.Replace("{remaining}", "", StringComparison.Ordinal);
        if (literal.Contains('{') || literal.Contains('}'))
            throw new InvalidDataException("restarts.warningMessage supports only the optional {remaining} placeholder.");
    }

    public static string RenderWarning(string template, int seconds)
    {
        ValidateWarningTemplate(template);
        if (seconds < 1) throw new InvalidDataException("Restart warnings require a positive number of seconds remaining.");
        var remaining = seconds % 60 == 0
            ? $"{seconds / 60} minute{(seconds == 60 ? "" : "s")}" : $"{seconds} second{(seconds == 1 ? "" : "s")}";
        var result = template.Replace("{remaining}", remaining, StringComparison.Ordinal);
        ValidateText(result, "Rendered restarts.warningMessage");
        return result;
    }
}
