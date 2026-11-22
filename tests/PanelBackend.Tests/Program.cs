using System.Diagnostics;
using System.IO.Compression;
using NomadDrive.Panel;
using NomadDrive.Server;

if (args.Length != 2) throw new ArgumentException("Pass a new fixture directory and the bundled supervisor executable.");
var root = Path.GetFullPath(args[0]);
if (Directory.Exists(root)) throw new IOException("Fixture directory must be new.");
Directory.CreateDirectory(root);
var configFile = Path.Combine(root, "profile with spaces.json");
SafeFiles.AtomicJson(configFile, new ServerConfig());
var config = ServerConfig.Load(configFile);
var backend = new PanelBackend(configFile, args[1]);
Directory.CreateDirectory(Path.Combine(config.GameDirectory, "BepInEx", "core"));
File.WriteAllText(Path.Combine(config.GameDirectory, ".nomaddrive-server-runtime"), "");
File.WriteAllText(Path.Combine(config.GameDirectory, "Nomad Drive.exe"), "fixture");
void Check(bool pass, string what)
{
    if (!pass) throw new Exception(what);
    Console.WriteLine("PASS " + what);
}
void Refused(Action action)
{
    try { action(); } catch (Exception ex) when (ex is IOException or InvalidDataException) { return; }
    throw new Exception("Expected operation to fail.");
}
string Zip(string fileName, params string[] names)
{
    var path = Path.Combine(root, fileName);
    using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
    foreach (var name in names)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open());
        writer.Write(name);
    }
    return path;
}
var status = await backend.StatusAsync();
Check(!status.Running && status.Names.Count == 0, "unused profile is offline");
Check((await backend.CommandAsync("validate")).Contains("valid"), "invoke real CLI against profile with spaces");
var source = Path.Combine(root, "Sample.dll");
File.Copy(typeof(PanelBackend).Assembly.Location, source);
backend.InstallMod(source);
var mod = backend.Mods().Single();
Check(mod.RelativePath == "Sample.dll" && mod.Enabled, "install standalone managed DLL");
backend.SetModEnabled(mod, false);
Check(!backend.Mods().Single().Enabled, "disable mod");
Refused(() => backend.InstallMod(source));
backend.SetModEnabled(backend.Mods().Single(), true);
var package = Zip("Garage.zip", "README.md", "BepInEx/plugins/Garage/Core.dll", "BepInEx/plugins/Garage/Textures/paint.png");
backend.InstallMod(package);
backend.SetModEnabled(backend.Mods().Single(m => m.RelativePath == "Garage"), false);
Check(File.Exists(Path.Combine(config.GameDirectory, "BepInEx/panel-disabled-plugins/Garage/Textures/paint.png")), "disable packaged dependencies and assets together");
Refused(() => backend.InstallMod(Zip("bad.zip", "BepInEx/plugins/bad/../escaped.dll")));
Refused(() => backend.InstallMod(Zip("partial.zip", "BepInEx/plugins/Garage2/A.dll", "BepInEx/patchers/also.dll")));
Refused(() => backend.InstallMod(Zip("extra.zip", "BepInEx/plugins/Garage2/A.dll", "winhttp.dll")));
Refused(() => backend.InstallMod(Zip("host.zip", "BepInEx/plugins/Garage2/NomadDrive.DedicatedServer.dll")));
Check(backend.Mods().Count == 2, "invalid and conflicting packages install nothing");
var required = Path.Combine(config.GameDirectory, "BepInEx/plugins/DedicatedServer");
Directory.CreateDirectory(required);
File.WriteAllText(Path.Combine(required, "NomadDrive.DedicatedServer.dll"), "fixture");
Refused(() => backend.SetModEnabled(backend.Mods().Single(m => m.Protected), false));
using (SafeFiles.AcquireLock(config.GameDirectory)) Refused(() => backend.SetModEnabled(mod, false));
Check(backend.Mods().Single(m => m.RelativePath == mod.RelativePath).Enabled, "host plugin and active runtime protected");

var runDir = Path.Combine(config.DataDirectory, "runs", "unit");
var control = Path.Combine(config.DataDirectory, "control", "unit");
Directory.CreateDirectory(Path.Combine(control, "requests"));
Directory.CreateDirectory(Path.Combine(control, "responses"));
var stamp = new SupervisorStatus { RunDirectory = runDir, ControlDirectory = control, State = "ready", Detail = "Attached" };
SafeFiles.AtomicJson(Path.Combine(config.DataDirectory, "supervisor.json"), stamp);
SafeFiles.AtomicJson(Path.Combine(runDir, "status.json"), new PluginStatus {
    State = "ready", UpdatedUtc = DateTimeOffset.UtcNow, LobbyCode = "TEST", Players = 1, MaxPlayers = 3,
    Roster = [new() { ConnectionId = 7, DisplayName = "Café" }]
});
status = await backend.StatusAsync();
Check(status.Running && status.PlayerCountKnown && status.Players == 1 && status.Names.Single() == "Café" && status.LobbyCode == "TEST", "fresh native roster and lobby status");
Check(await backend.StartAsync() == "Attached", "start attaches to live supervisor");
const string announcement = "Check the \"garage\" & save";
var operation = backend.CommandAsync("say", announcement);
var watch = Stopwatch.StartNew();
string? requestFile;
do
{
    requestFile = Directory.GetFiles(Path.Combine(control, "requests"), "*.json").SingleOrDefault();
    if (watch.Elapsed.TotalSeconds > 10) throw new TimeoutException("No real control request.");
    await Task.Delay(40);
} while (requestFile is null);
var command = Protocol.Read<CommandRequest>(requestFile)!;
Check(command.Command == "say" && command.Text == announcement, "real CLI passes chat through file IPC with quotes and metacharacters");
SafeFiles.AtomicJson(Path.Combine(control, "responses", command.Id + ".json"), new CommandResponse { Id = command.Id, Ok = true, Message = "delivered" });
Check(await operation == "delivered", "acknowledged operation result");
stamp.State = "recovering";
SafeFiles.AtomicJson(Path.Combine(config.DataDirectory, "supervisor.json"), stamp);
status = await backend.StatusAsync();
Check(status.Running && status.State == "recovering" && !status.PlayerCountKnown && status.Names.Count == 0, "old run roster hidden during recovery");
stamp.State = "ready";
SafeFiles.AtomicJson(Path.Combine(config.DataDirectory, "supervisor.json"), stamp);
var gamePath = Path.Combine(runDir, "status.json");
var heartbeat = Protocol.Read<PluginStatus>(gamePath)!;
heartbeat.UpdatedUtc = DateTimeOffset.UtcNow.AddSeconds(-config.CommandTimeoutSeconds - 1);
SafeFiles.AtomicJson(gamePath, heartbeat);
status = await backend.StatusAsync();
Check(!status.PlayerCountKnown && status.Names.Count == 0 && status.LobbyCode == "", "stale heartbeat reports unknown occupancy and hides former lobby");
File.Move(gamePath, gamePath + ".unavailable");
status = await backend.StatusAsync();
Check(!status.PlayerCountKnown && status.State == "attention" && status.LobbyCode == "", "missing heartbeat does not report a confirmed empty lobby");
File.WriteAllLines(Path.Combine(runDir, "player.log"), Enumerable.Range(1, 500).Select(n => "line " + n));
var tail = backend.TailLog("Game");
Check(tail.Contains("line 500") && !tail.Contains("line 50\r"), "bounded game log tail");
stamp.SupervisorStartedUtc = DateTime.UnixEpoch;
SafeFiles.AtomicJson(Path.Combine(config.DataDirectory, "supervisor.json"), stamp);
status = await backend.StatusAsync();
Check(!status.Running && status.Names.Count == 0 && status.LobbyCode == "", "stale supervisor never presents past players as online");
await backend.StopIfRunningAsync();

stamp.SupervisorStartedUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime();
SafeFiles.AtomicJson(Path.Combine(config.DataDirectory, "supervisor.json"), stamp);
async Task RespondToStopAsync(bool success)
{
    var deadline = Stopwatch.StartNew();
    while (deadline.Elapsed < TimeSpan.FromSeconds(10))
    {
        foreach (var file in Directory.GetFiles(Path.Combine(control, "requests"), "*.json"))
        {
            var pending = Protocol.Read<CommandRequest>(file);
            if (pending?.Command != "stop") continue;
            var response = Path.Combine(control, "responses", pending.Id + ".json");
            if (File.Exists(response)) continue;
            SafeFiles.AtomicJson(response,
                new CommandResponse { Id = pending.Id, Ok = success, Error = "Test save failed" });
            if (success)
            {
                await Task.Delay(300);
                stamp.SupervisorStartedUtc = DateTime.UnixEpoch;
                SafeFiles.AtomicJson(Path.Combine(config.DataDirectory, "supervisor.json"), stamp);
            }
            return;
        }
        await Task.Delay(40);
    }
    throw new TimeoutException("No stop request received.");
}
var refusal = RespondToStopAsync(false);
try { await backend.StopIfRunningAsync(); throw new Exception("Stop failure was ignored."); }
catch (IOException e) when (e.Message == "Test save failed") { }
await refusal;
Check((await backend.StatusAsync()).Running, "failed save preserves the running server and surfaces the error");
await Task.WhenAll(RespondToStopAsync(true), backend.StopIfRunningAsync());
Check(!(await backend.StatusAsync()).Running, "quit waits for the supervisor to finish stopping");
