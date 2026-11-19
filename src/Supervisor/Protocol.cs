using System.Diagnostics;
using System.Text.Json;

namespace NomadDrive.Server;

public sealed class CommandRequest
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Command { get; set; } = "";
    public string Text { get; set; } = "";
}
public sealed class CommandResponse
{
    public string Id { get; set; } = "";
    public bool Ok { get; set; }
    public string Error { get; set; } = "";
    public string SlotId { get; set; } = "";
    public string SnapshotPath { get; set; } = "";
    public string Message { get; set; } = "";
}
public sealed class PluginStatus
{
    public bool TerminalNetworkStop { get; set; }
    public string State { get; set; } = "starting";
    public string Detail { get; set; } = "";
    public string SlotId { get; set; } = "";
    public int Players { get; set; }
    public int MaxPlayers { get; set; }
    public string LobbyCode { get; set; } = "";
    public DateTimeOffset UpdatedUtc { get; set; }
}

public static class HostHealth
{
    public static string? Problem(PluginStatus? status, DateTimeOffset now, TimeSpan heartbeatGrace)
    {
        if (status is null) return "Hosting plugin status is missing; scheduled operations are suspended.";
        if (status.State == "error") return "Hosting plugin error: " + status.Detail;
        if (now - status.UpdatedUtc > heartbeatGrace) return "Hosting plugin heartbeat is stale; scheduled operations are suspended.";
        if (status.State != "ready") return "Hosting plugin is not ready (" + status.State + "): " + status.Detail;
        return null;
    }
}
public sealed class SupervisorStatus
{
    public string State { get; set; } = "stopped";
    public string Detail { get; set; } = "";
    public string RunDirectory { get; set; } = "";
    public string ControlDirectory { get; set; } = "";
    public int SupervisorPid { get; set; } = Environment.ProcessId;
    public DateTime SupervisorStartedUtc { get; set; } = Process.GetCurrentProcess().StartTime.ToUniversalTime();
    public int GamePid { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public PluginStatus? Game { get; set; }
}

public static class Protocol
{
    // A plugin may extend its responses without breaking this supervisor.
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static T? Read<T>(string path, bool tolerateSharingViolation = false)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                SafeFiles.RejectReparseAncestors(path);
                // Mono's File.Replace needs delete sharing on the destination. ReadAllText
                // uses FileShare.Read and can make either the reader or the replacement fail.
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
                return JsonSerializer.Deserialize<T>(stream, Json);
            }
            catch (FileNotFoundException) { return default; }
            catch (DirectoryNotFoundException) { return default; }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
            {
                if (attempt >= 5)
                {
                    // Polling callers can try again on their next tick. Startup guards
                    // deliberately fail closed rather than treating a locked record as absent.
                    if (tolerateSharingViolation) return default;
                    throw;
                }
                Thread.Sleep(5);
            }
        }
    }
    public static bool ValidId(string id) => Guid.TryParseExact(id, "N", out _);

    public static async Task<CommandResponse> SendAsync(string directory, CommandRequest request, TimeSpan timeout,
        Func<bool>? peerExited = null, CancellationToken cancellation = default)
    {
        if (!ValidId(request.Id)) throw new InvalidDataException("Invalid request ID.");
        var responsePath = Path.Combine(directory, "responses", request.Id + ".json");
        SafeFiles.AtomicJson(Path.Combine(directory, "requests", request.Id + ".json"), request);
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            var response = Read<CommandResponse>(responsePath, tolerateSharingViolation: true);
            if (response is not null)
            {
                if (response.Id != request.Id) throw new InvalidDataException("IPC response ID mismatch.");
                return response;
            }
            if (peerExited?.Invoke() == true) throw new IOException("Game process exited before acknowledging the command.");
            await Task.Delay(100, cancellation);
        }
        throw new TimeoutException($"Timed out waiting for '{request.Command}'. It may still be running; no forced termination was attempted.");
    }
}

public interface IGameSession
{
    string RunDirectory { get; }
    bool HasExited { get; }
    Task<CommandResponse> SendAsync(string command, string text = "");
    Task<bool> WaitForExitAsync(TimeSpan timeout);
}

public sealed class GameSession(Process process, string runDirectory, int commandTimeoutSeconds) : IGameSession, IDisposable
{
    public string RunDirectory { get; } = runDirectory;
    public bool HasExited => process.HasExited;
    public int ProcessId => process.Id;
    public int ExitCode => process.ExitCode;
    public Task<CommandResponse> SendAsync(string command, string text = "") =>
        Protocol.SendAsync(RunDirectory, new CommandRequest { Command = command, Text = text },
            TimeSpan.FromSeconds(commandTimeoutSeconds), () => HasExited);

    public async Task<bool> WaitForExitAsync(TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try { await process.WaitForExitAsync(cancellation.Token); return true; }
        catch (OperationCanceledException) { return false; }
    }
    public void TerminateFailedProcess()
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
    }
    public void Dispose() => process.Dispose();
}

public sealed class LiveRuntime
{
    public int GamePid { get; set; }
    public DateTime GameStartedUtc { get; set; }
    public string RunDirectory { get; set; } = "";
}

public static class RuntimeGuard
{
    public static void RejectLiveGame(string data)
    {
        var record = Protocol.Read<LiveRuntime>(Path.Combine(data, "active-runtime.json"));
        if (record is null) return;
        try
        {
            using var process = Process.GetProcessById(record.GamePid);
            if (!process.HasExited && Math.Abs((process.StartTime.ToUniversalTime() - record.GameStartedUtc).TotalSeconds) < 2)
                throw new IOException($"A game process from an earlier supervisor is still running (PID {record.GamePid}). Stop that instance before using this data directory.");
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
    }
}
