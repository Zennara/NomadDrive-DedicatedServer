namespace NomadDrive.Server;

public sealed class ServerOperations(IGameSession game, ServerConfig config, Action<string> log, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    public bool ShutdownAcknowledged { get; private set; }
    public bool CommandsUncertain { get; private set; }

    public async Task<CommandResponse> ExecuteAsync(string command, string text = "")
    {
        if (ShutdownAcknowledged) return Failure("Shutdown was already acknowledged. Waiting for the game to exit.");
        if (CommandsUncertain) return Failure("An earlier command timed out. Its result is unknown; operator intervention is required before more save or shutdown commands.");
        try
        {
            switch (command)
            {
                case "save": return await game.SendAsync("save");
                case "say":
                    if (string.IsNullOrWhiteSpace(text) || text.Length > 256 || text.Any(char.IsControl))
                        return Failure("Chat text must be 1..256 characters without control characters.");
                    return await game.SendAsync("say", text);
                case "backup":
                    if (config.Backups.MessagesEnabled)
                    {
                        var notice = await game.SendAsync("say", config.Backups.StartMessage);
                        if (!notice.Ok) log("Backup notice failed: " + notice.Error);
                    }
                    var snapshot = await game.SendAsync("snapshot");
                    if (!snapshot.Ok) return snapshot;
                    if (string.IsNullOrEmpty(snapshot.SnapshotPath)) return Failure("Plugin did not return a snapshot path.");
                    var archive = new BackupStore(Path.Combine(config.DataDirectory, "backups"), config.Backups.Keep)
                        .Create(snapshot.SnapshotPath, game.RunDirectory, _clock.GetUtcNow());
                    log("Backup completed: " + archive);
                    if (config.Backups.MessagesEnabled)
                    {
                        var notice = await game.SendAsync("say", config.Backups.CompletedMessage);
                        if (!notice.Ok) log("Backup notice failed: " + notice.Error);
                    }
                    return new CommandResponse { Ok = true, SlotId = snapshot.SlotId, Message = archive };
                case "stop":
                case "restart":
                    if (config.Restarts.MessagesEnabled)
                    {
                        var notice = await game.SendAsync("say", command == "restart" ? config.Restarts.RestartingMessage : config.Restarts.StoppingMessage);
                        if (!notice.Ok) log("Chat notice failed: " + notice.Error);
                    }
                    // Shutdown owns the save operation. A failed save never authorizes terminating the process.
                    var result = await game.SendAsync("shutdown");
                    if (!result.Ok) return result;
                    ShutdownAcknowledged = true;
                    if (!await game.WaitForExitAsync(TimeSpan.FromSeconds(config.ShutdownTimeoutSeconds)))
                        return Failure("The game acknowledged its completed save but did not exit. It has not been killed. Stop/restart needs operator intervention.");
                    return new CommandResponse { Ok = true, SlotId = result.SlotId, Message = command == "restart" ? "Saved; restarting." : "Saved; stopped." };
                default: return Failure("Unknown command: " + command);
            }
        }
        catch (TimeoutException ex)
        {
            CommandsUncertain = true;
            log(ex.Message);
            return Failure(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            log(ex.Message);
            return Failure(ex.Message);
        }
    }

    private static CommandResponse Failure(string error) => new() { Ok = false, Error = error };
}

public sealed class CrashBudget(RecoveryConfig config, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Queue<DateTimeOffset> _attempts = new();
    public bool TrySpend()
    {
        if (!config.Enabled) return false;
        var now = _clock.GetUtcNow();
        while (_attempts.TryPeek(out var timestamp) && now - timestamp >= TimeSpan.FromMinutes(config.WindowMinutes)) _attempts.Dequeue();
        if (_attempts.Count >= config.MaxAttempts) return false;
        _attempts.Enqueue(now);
        return true;
    }
}

public sealed record DueWork(bool Backup, bool Restart, int[] Warnings);

public sealed class MaintenanceSchedule(ServerConfig config, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private DateTimeOffset _nextBackup;
    private DateTimeOffset _nextRestart;
    private readonly HashSet<int> _sentWarnings = new();

    public void Begin()
    {
        var now = _clock.GetUtcNow();
        _nextBackup = now.AddMinutes(config.Backups.IntervalMinutes);
        _nextRestart = now.AddHours(config.Restarts.IntervalHours);
        _sentWarnings.Clear();
    }

    public DueWork Poll()
    {
        var now = _clock.GetUtcNow();
        bool backup = config.Backups.Enabled && now >= _nextBackup;
        bool restart = config.Restarts.Enabled && now >= _nextRestart;
        if (backup) _nextBackup = now.AddMinutes(config.Backups.IntervalMinutes);
        var remaining = (_nextRestart - now).TotalSeconds;
        var warnings = config.Restarts.Enabled && config.Restarts.MessagesEnabled && !restart
            ? config.Restarts.WarningSeconds.Where(s => remaining <= s && !_sentWarnings.Contains(s)).OrderDescending().ToArray()
            : [];
        foreach (var seconds in warnings) _sentWarnings.Add(seconds);
        // If a long save crosses several warning thresholds, send only the most urgent remaining warning.
        if (warnings.Length > 1) warnings = [warnings[^1]];
        if (restart)
        {
            _nextRestart = now.AddHours(config.Restarts.IntervalHours);
            _sentWarnings.Clear();
        }
        return new DueWork(backup, restart, warnings);
    }
}
