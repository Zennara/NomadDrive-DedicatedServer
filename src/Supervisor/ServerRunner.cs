using System.Diagnostics;

namespace NomadDrive.Server;

public sealed class ServerRunner(ServerConfig config, Action<string> log)
{
    private readonly SupervisorStatus _status = new();
    private volatile bool _stopRequested;
    public void RequestStop() => _stopRequested = true;

    public async Task<int> RunAsync()
    {
        config.Validate(requireRuntime: true);
        using var instanceLock = SafeFiles.AcquireLock(config.DataDirectory);
        using var runtimeLock = SafeFiles.AcquireLock(config.GameDirectory);
        RuntimeGuard.RejectLiveGame(config.DataDirectory);
        RuntimeGuard.RejectLiveGame(config.GameDirectory);
        SafeFiles.ReadWorld(config.DataDirectory);
        _status.ControlDirectory = Path.Combine(config.DataDirectory, "control", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_status.ControlDirectory, "requests"));
        Directory.CreateDirectory(Path.Combine(_status.ControlDirectory, "responses"));
        var crashes = new CrashBudget(config.Recovery);
        bool recover = false;
        try
        {
            while (!_stopRequested)
            {
                if (recover)
                {
                    if (!crashes.TrySpend())
                    {
                        SetStatus("error", "Crash recovery retry limit reached; inspect the run logs before starting again.");
                        return 1;
                    }
                    SetStatus("recovering", $"Restarting after failure in {config.Recovery.DelaySeconds} seconds.");
                    for (var remaining = config.Recovery.DelaySeconds * 10; remaining > 0 && !_stopRequested; remaining--) await Task.Delay(100);
                    if (_stopRequested) break;
                }
                SafeFiles.ReadWorld(config.DataDirectory);
                using var game = Launch();
                if (!await WaitReadyAsync(game))
                {
                    if (!game.HasExited)
                    {
                        log("Terminating failed startup. No ready server session was established.");
                        game.TerminateFailedProcess();
                        if (!await game.WaitForExitAsync(TimeSpan.FromSeconds(config.ShutdownTimeoutSeconds)))
                            throw new IOException("Failed startup process did not terminate; refusing to launch another process.");
                    }
                    recover = true;
                    continue;
                }
                var operations = new ServerOperations(game, config, log);
                var schedule = new MaintenanceSchedule(config);
                schedule.Begin();
                SetStatus("ready", "Accepting commands.");
                string? plannedExit = null;
                bool wasUnhealthy = false;
                string? recoveryBlocked = null;
                int checkpointLockRetries = 0;
                DateTimeOffset nextCheckpointCheck = DateTimeOffset.MinValue;
                while (!game.HasExited)
                {
                    _status.Game = Protocol.Read<PluginStatus>(Path.Combine(game.RunDirectory, "status.json"), tolerateSharingViolation: true);
                    var healthProblem = HostHealth.Problem(_status.Game, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(config.CommandTimeoutSeconds));
                    if (recoveryBlocked == null && DateTimeOffset.UtcNow >= nextCheckpointCheck && !_stopRequested && plannedExit == null &&
                        _status.Game is { State: "error", TerminalNetworkStop: true, Players: 0 } terminal &&
                        !operations.CommandsUncertain && !operations.ShutdownRequested)
                    {
                        bool checkpointPinned = false;
                        try
                        {
                            if (!crashes.HasAttempt())
                                throw new InvalidDataException("No automatic crash recovery attempts are available. Inspect the failed run before restarting.");
                            await SafeFiles.WithVerifiedCheckpointAsync(config.DataDirectory, terminal.SlotId, async () =>
                            {
                                checkpointPinned = true;
                                log("Host networking stopped. Verified the completed checkpoint; terminating the offline game without another save.");
                                game.TerminateFailedProcess();
                                if (!await game.WaitForExitAsync(TimeSpan.FromSeconds(config.ShutdownTimeoutSeconds)))
                                    throw new TimeoutException("The offline game did not terminate. No replacement game was launched.");
                            });
                            break;
                        }
                        catch (IOException ex) when (!checkpointPinned && (ex.HResult & 0xffff) is 32 or 33 && checkpointLockRetries < 5)
                        {
                            nextCheckpointCheck = DateTimeOffset.UtcNow.AddMilliseconds(250 << checkpointLockRetries++);
                            log("Checkpoint verification encountered a sharing violation; retrying while the host remains offline.");
                        }
                        catch (Exception ex) when (!checkpointPinned && ex is (IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException))
                        {
                            recoveryBlocked = "Offline host recovery blocked: " + ex.Message;
                        }
                    }
                    healthProblem = recoveryBlocked ?? healthProblem;
                    if (healthProblem is not null) SetStatus("attention", healthProblem);
                    else if (wasUnhealthy && !operations.CommandsUncertain && !operations.ShutdownAcknowledged) SetStatus("ready", "Hosting plugin heartbeat recovered; accepting commands.");
                    wasUnhealthy = healthProblem is not null;
                    if (_stopRequested)
                    {
                        _stopRequested = false;
                        var stop = await operations.ExecuteAsync("stop");
                        if (stop.Ok) { plannedExit = "stop"; break; }
                        if (operations.ShutdownAcknowledged) plannedExit = "stop";
                        SetStatus("attention", "Graceful stop failed: " + stop.Error);
                        log("The game remains running. Resolve the error before retrying stop.");
                    }
                    var action = await ProcessControlAsync(operations);
                    if (action is not null)
                    {
                        plannedExit = action;
                        if (game.HasExited) break;
                    }
                    if (healthProblem is null && !operations.CommandsUncertain && !operations.ShutdownAcknowledged)
                    {
                        var due = schedule.Poll();
                        foreach (var seconds in due.Warnings)
                        {
                            var warning = await operations.ExecuteAsync("say", ChatMessages.RenderWarning(config.Restarts.WarningMessage, seconds));
                            if (!warning.Ok) log("Restart warning failed: " + warning.Error);
                        }
                        if (due.Backup)
                        {
                            var backup = await operations.ExecuteAsync("backup");
                            if (!backup.Ok) log("Scheduled backup failed: " + backup.Error);
                        }
                        if (due.Restart && !operations.CommandsUncertain)
                        {
                            SetStatus("restarting", "Saving before scheduled restart.");
                            var restart = await operations.ExecuteAsync("restart");
                            if (restart.Ok || operations.ShutdownAcknowledged) plannedExit = "restart";
                            if (!restart.Ok) SetStatus("attention", "Scheduled restart failed: " + restart.Error);
                        }
                    }
                    if (operations.CommandsUncertain) SetStatus("attention", "A command timed out. No further save or shutdown commands will be issued; inspect the game log.");
                    else PublishStatus();
                    await Task.Delay(250);
                }
                _status.GamePid = 0;
                if (operations.RequiresManualExitReview)
                {
                    SetStatus("attention", "The game exited after a shutdown request timed out. Its result is unconfirmed; automatic restart is disabled. Check the final save and plugin logs before starting again.");
                    return 1;
                }
                if (plannedExit == "stop")
                {
                    SetStatus("stopped", "Completed save and graceful shutdown.");
                    return 0;
                }
                if (plannedExit == "restart")
                {
                    log("Completed save and graceful shutdown; launching the same world.");
                    recover = false;
                }
                else
                {
                    log($"Game exited unexpectedly with exit code {game.ExitCode}. No backup was made from unsaved state.");
                    recover = true;
                }
            }
            SetStatus("stopped", "Stopped before starting a session.");
            return 0;
        }
        catch (Exception ex)
        {
            SetStatus("error", ex.Message + " Any surviving game process has been left running to avoid losing data.");
            return 1;
        }
    }

    private GameSession Launch()
    {
        RuntimeGuard.RejectLiveGame(config.DataDirectory);
        RuntimeGuard.RejectLiveGame(config.GameDirectory);
        var run = Path.Combine(config.DataDirectory, "runs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(run, "requests"));
        Directory.CreateDirectory(Path.Combine(run, "responses"));
        var runtimeConfig = Path.Combine(run, "startup-config.json");
        SafeFiles.AtomicJson(runtimeConfig, config);
        var start = new ProcessStartInfo(Path.Combine(config.GameDirectory, "Nomad Drive.exe"))
        {
            WorkingDirectory = config.GameDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add("-batchmode");
        if (config.Headless) start.ArgumentList.Add("-nographics");
        else
        {
            start.ArgumentList.Add("-screen-width"); start.ArgumentList.Add("640");
            start.ArgumentList.Add("-screen-height"); start.ArgumentList.Add("480");
        }
        start.ArgumentList.Add("-logFile"); start.ArgumentList.Add(Path.Combine(run, "player.log"));
        start.Environment["NOMADDRIVE_SERVER_CONFIG"] = runtimeConfig;
        start.Environment["NOMADDRIVE_SERVER_RUN"] = run;
        start.Environment["NOMADDRIVE_SERVER_DATA"] = config.DataDirectory;
        var process = Process.Start(start) ?? throw new IOException("Failed to launch Nomad Drive.");
        try
        {
            // Registration happens before WaitReadyAsync and before ownership transfers
            // to GameSession. Abort this new child if any registration write fails.
            return StartupRegistration.Complete(process, () =>
            {
                var live = new LiveRuntime
                {
                    GamePid = process.Id, GameStartedUtc = process.StartTime.ToUniversalTime(), RunDirectory = run
                };
                SafeFiles.AtomicJson(Path.Combine(config.DataDirectory, "active-runtime.json"), live);
                SafeFiles.AtomicJson(Path.Combine(config.GameDirectory, "active-runtime.json"), live);
                _status.RunDirectory = run;
                _status.GamePid = process.Id;
                _status.Game = null;
                SetStatus("starting", $"Game process {process.Id} launched. Waiting for the hosting plugin.");
                return new GameSession(process, run, config.CommandTimeoutSeconds);
            }, TimeSpan.FromSeconds(config.ShutdownTimeoutSeconds));
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private async Task<bool> WaitReadyAsync(GameSession game)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(config.StartupTimeoutSeconds) && !_stopRequested)
        {
            if (game.HasExited) { log($"Game exited during startup, code {game.ExitCode}."); return false; }
            var status = Protocol.Read<PluginStatus>(Path.Combine(game.RunDirectory, "status.json"), tolerateSharingViolation: true);
            _status.Game = status;
            if (status?.State == "ready") return true;
            if (status?.State == "error") { log("Hosting plugin startup failed: " + status.Detail); return false; }
            PublishStatus();
            await Task.Delay(250);
        }
        log(_stopRequested ? "Startup canceled by operator." : "Hosting plugin startup timed out.");
        return false;
    }

    private async Task<string?> ProcessControlAsync(ServerOperations operations)
    {
        foreach (var path in Directory.EnumerateFiles(Path.Combine(_status.ControlDirectory, "requests"), "*.json").Order(StringComparer.Ordinal))
        {
            var id = Path.GetFileNameWithoutExtension(path);
            if (!Protocol.ValidId(id)) continue;
            var responsePath = Path.Combine(_status.ControlDirectory, "responses", id + ".json");
            if (File.Exists(responsePath)) continue;
            CommandResponse response;
            CommandRequest? request = null;
            try
            {
                if (new FileInfo(path).Length > 16384) throw new InvalidDataException("Command request is too large.");
                request = Protocol.Read<CommandRequest>(path) ?? throw new InvalidDataException("Empty command request.");
                if (request.Id != id) throw new InvalidDataException("Command request ID mismatch.");
                response = await operations.ExecuteAsync(request.Command, request.Text);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
            {
                response = new CommandResponse { Ok = false, Error = ex.Message };
            }
            response.Id = id;
            SafeFiles.AtomicJson(responsePath, response);
            if (!response.Ok) log("Command failed: " + response.Error);
            if (request?.Command is "stop" or "restart" && (response.Ok || operations.ShutdownAcknowledged)) return request.Command;
        }
        return null;
    }

    private void SetStatus(string state, string detail)
    {
        var changed = _status.State != state || _status.Detail != detail;
        _status.State = state;
        _status.Detail = detail;
        if (changed) log(detail);
        PublishStatus();
    }
    private void PublishStatus()
    {
        _status.UpdatedUtc = DateTimeOffset.UtcNow;
        SafeFiles.AtomicJson(Path.Combine(config.DataDirectory, "supervisor.json"), _status);
    }
}
