using System.Diagnostics;
using System.Text.Json;

namespace NomadDrive.Server;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h") { Help(); return 0; }
        try
        {
            var command = args[0];
            ValidateOptions(args, command);
            var path = Path.GetFullPath(Option(args, "--config") ?? "server.json");
            if (command == "init")
            {
                if (File.Exists(path)) throw new IOException("Config already exists; it was not overwritten.");
                SafeFiles.AtomicJson(path, new ServerConfig());
                Console.WriteLine("Created " + path);
                return 0;
            }
            var config = ServerConfig.Load(path, requireRuntime: command == "run");
            switch (command)
            {
                case "validate":
                    SafeFiles.ReadWorld(config.DataDirectory);
                    Console.WriteLine("Config and selected world paths are valid. Runtime/plugin compatibility is checked at startup.");
                    return 0;
                case "import":
                    var source = Option(args, "--source") ?? throw new ArgumentException("import requires --source <save-slot-directory>.");
                    var slot = SafeFiles.ImportSlot(source, config.DataDirectory);
                    Console.WriteLine("Imported a copy as " + slot + ". The source was not modified; the game validates its format at startup.");
                    return 0;
                case "status":
                    var status = Protocol.Read<SupervisorStatus>(Path.Combine(config.DataDirectory, "supervisor.json"), tolerateSharingViolation: true);
                    if (status is null) { Console.WriteLine("No supervisor status is available."); return 1; }
                    if (SafeFiles.Inside(status.RunDirectory, Path.Combine(config.DataDirectory, "runs")))
                    {
                        status.Game = Protocol.Read<PluginStatus>(Path.Combine(status.RunDirectory, "status.json"), tolerateSharingViolation: true);
                        if (status.State is "ready" or "attention")
                        {
                            var problem = HostHealth.Problem(status.Game, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(config.CommandTimeoutSeconds));
                            if (problem is not null) { status.State = "attention"; status.Detail = problem; }
                        }
                    }
                    Console.WriteLine(JsonSerializer.Serialize(status, ServerConfig.Json));
                    if (!SupervisorIsAlive(status)) { Console.WriteLine("Supervisor is not running. Displayed status is its last recorded state."); return 1; }
                    return 0;
                case "run":
                    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Running the game is supported on Windows only.");
                    SafeFiles.RejectReparseAncestors(config.DataDirectory);
                    Directory.CreateDirectory(Path.Combine(config.DataDirectory, "logs"));
                    using (var writer = new StreamWriter(Path.Combine(config.DataDirectory, "logs", $"supervisor-{DateTime.UtcNow:yyyyMMddTHHmmssfffffffZ}.log"), false) { AutoFlush = true })
                    {
                        void Log(string line)
                        {
                            var entry = $"{DateTimeOffset.UtcNow:O} {line}";
                            Console.WriteLine(entry);
                            writer.WriteLine(entry);
                        }
                        var runner = new ServerRunner(config, Log);
                        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; runner.RequestStop(); };
                        Console.CancelKeyPress += cancel;
                        try { return await runner.RunAsync(); }
                        finally { Console.CancelKeyPress -= cancel; }
                    }
                case "say": case "save": case "backup": case "restart": case "stop":
                    return await SendControlAsync(config, command, Option(args, "--text") ?? "");
                default: throw new ArgumentException("Unknown command: " + command);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException or TimeoutException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static async Task<int> SendControlAsync(ServerConfig config, string command, string text)
    {
        var status = Protocol.Read<SupervisorStatus>(Path.Combine(config.DataDirectory, "supervisor.json"), tolerateSharingViolation: true);
        if (status is null || !SupervisorIsAlive(status)) throw new IOException("No running supervisor was found for this data directory.");
        if (!SafeFiles.Inside(status.ControlDirectory, Path.Combine(config.DataDirectory, "control"))) throw new InvalidDataException("Invalid control directory in supervisor status.");
        if (status.State is "starting" or "recovering") throw new IOException("Server is still starting. Try the command when status is ready; Ctrl+C can cancel startup in its supervisor console.");
        var response = await Protocol.SendAsync(status.ControlDirectory, new CommandRequest { Command = command, Text = text },
            TimeSpan.FromSeconds(config.CommandTimeoutSeconds * 3 + config.ShutdownTimeoutSeconds + 30), () => !SupervisorIsAlive(status));
        Console.WriteLine(JsonSerializer.Serialize(response, ServerConfig.Json));
        return response.Ok ? 0 : 1;
    }

    private static bool SupervisorIsAlive(SupervisorStatus status)
    {
        try
        {
            using var process = Process.GetProcessById(status.SupervisorPid);
            return !process.HasExited && Math.Abs((process.StartTime.ToUniversalTime() - status.SupervisorStartedUtc).TotalSeconds) < 2;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0) return null;
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException(name + " requires a value.");
        return args[index + 1];
    }

    private static void ValidateOptions(string[] args, string command)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index += 2)
        {
            var name = args[index];
            if (!(name == "--config" || name == "--source" && command == "import" || name == "--text" && command == "say"))
                throw new ArgumentException("Unknown option for " + command + ": " + name);
            if (!seen.Add(name)) throw new ArgumentException("Duplicate option: " + name);
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException(name + " requires a value.");
        }
    }

    private static void Help() => Console.WriteLine("""
        Nomad Drive dedicated server supervisor (Windows)
        init      [--config server.json]                  Create an example startup config
        validate  [--config server.json]                  Validate config and selected save
        run       [--config server.json]                  Host until a graceful stop
        status    [--config server.json]                  Show supervisor and game status
        say       [--config server.json] --text "message"  Send a server chat message
        save      [--config server.json]                  Wait for a completed game save
        backup    [--config server.json]                  Save, snapshot, and archive
        restart   [--config server.json]                  Save, stop, and resume same world
        stop      [--config server.json]                  Save and stop
        import    [--config server.json] --source <slot>   Copy a slot into an unused data directory

        Relative config paths resolve against the config file's directory.
        Ctrl+C requests a graceful save and stop. Failed or timed-out saves never authorize a forced stop.
        """);
}
