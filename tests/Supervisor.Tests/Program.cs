using System.IO.Compression;
using System.Diagnostics;
using System.Text.Json;
using NomadDrive.Server;
using NomadDrive.DedicatedServer;

if (args.SequenceEqual(["--registration-child"]))
{
    Thread.Sleep(TimeSpan.FromMinutes(1));
    return 0;
}

var root = Path.Combine(Environment.GetEnvironmentVariable("NOMADDRIVE_TEST_ROOT")
    ?? "E:/Odin/scratch/nomad-drive/dedicated-server/tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed = 0, failed = 0;
Console.WriteLine("Test evidence: " + root);

async Task Check(string name, Func<Task> action)
{
    try { await action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + Environment.NewLine + ex); }
}
Task Sync(Action action) { action(); return Task.CompletedTask; }
void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
ServerConfig Config(string label)
{
    return new ServerConfig { GameDirectory = Path.Combine(root, label, "game"), DataDirectory = Path.Combine(root, label, "data") };
}
async Task<(int ExitCode, string Output, string Error)> Cli(params string[] arguments)
{
    var output = Console.Out;
    var error = Console.Error;
    using var capturedOutput = new StringWriter();
    using var capturedError = new StringWriter();
    Console.SetOut(capturedOutput);
    Console.SetError(capturedError);
    try
    {
        var exitCode = await NomadDrive.Server.Program.Main(arguments);
        return (exitCode, capturedOutput.ToString(), capturedError.ToString());
    }
    finally { Console.SetOut(output); Console.SetError(error); }
}

await Check("descriptive public lobby names preserve Nomad settings", () => Sync(() =>
{
    var config = Config("long-lobby");
    config.Lobby.Name = "Zen's 24/7 Drive - Custom Plugins";
    config.Lobby.Public = true;
    config.World.Preset = "Nomad";
    config.Validate(false);
    config.Lobby.Name = new string('x', 65);
    Throws<InvalidDataException>(() => config.Validate(false));
}));
await Check("spawn reference follows arrival order rather than recycled IDs", () => Sync(() =>
{
    var order = new ConnectionOrder<TestConnection>();
    var first = new TestConnection(90);
    var second = new TestConnection(2);
    order.Add(first); order.Add(second); order.Add(first);
    Require(ReferenceEquals(order.FirstEligible(_ => true), first), "Oldest connection lost priority.");
    Require(order.Snapshot().Length == 2, "Duplicate registration added an entry.");
    order.Remove(first);
    Require(ReferenceEquals(order.FirstEligible(_ => true), second), "Disconnect did not hand over.");
    var returning = new TestConnection(90);
    order.Add(returning);
    Require(ReferenceEquals(order.FirstEligible(_ => true), second), "Returning player reclaimed priority.");
    order.Remove(second);
    var recycled = new TestConnection(2);
    order.Add(recycled);
    order.Remove(second);
    Require(ReferenceEquals(order.FirstEligible(_ => true), returning), "Recycled ID changed arrival order.");
    Require(order.Snapshot().Contains(recycled), "Old disconnect removed a new connection.");
}));
await Check("spawn reference skips unavailable players and the joining player", () => Sync(() =>
{
    var order = new ConnectionOrder<TestConnection>();
    var loading = new TestConnection(1);
    var joining = new TestConnection(2);
    var active = new TestConnection(3);
    order.Add(loading); order.Add(joining); order.Add(active);
    Require(ReferenceEquals(order.FirstEligible(c => c != loading && c != joining), active), "Ineligible player selected.");
    order.Remove(active);
    Require(order.FirstEligible(c => c != loading && c != joining) == null, "Missing reference did not permit fallback.");
    Require(order.Snapshot().SequenceEqual(new[] { loading, joining }), "Pending admission order changed.");
    order.Remove(loading); order.Remove(joining);
    Require(order.FirstEligible(_ => true) == null, "Empty session retained a reference.");
}));
await Check("config rejects unknown properties and case mistakes", () => Sync(() =>
{
    var path = Path.Combine(root, "unknown.json");
    File.WriteAllText(path, "{\"lobby\":{\"maxplayers\":8}}");
    Throws<JsonException>(() => ServerConfig.Load(path));
}));
await Check("config validates player, density, schedule and null boundaries", () => Sync(() =>
{
    var config = Config("bounds");
    config.Lobby.MaxPlayers = 1; config.Validate();
    config.Lobby.MaxPlayers = 63; config.Validate();
    config.Lobby.MaxPlayers = 64; Throws<InvalidDataException>(() => config.Validate());
    config.Lobby.MaxPlayers = 4; config.World.FoodDensity = 27; Throws<InvalidDataException>(() => config.Validate());
    config.World.FoodDensity = 25; config.Restarts.WarningSeconds = [60, 60]; Throws<InvalidDataException>(() => config.Validate());
    config.Restarts.WarningSeconds = [60]; config.World = null!; Throws<InvalidDataException>(() => config.Validate());
}));
await Check("public lobby JSON loads and round-trips as public true", () => Sync(() =>
{
    var path = Path.Combine(root, "public.json");
    File.WriteAllText(path, "{\"lobby\":{\"public\":true}}");
    var config = ServerConfig.Load(path);
    Require(config.Lobby.Public, "Public lobby request was not loaded.");
    var serialized = JsonSerializer.Serialize(config, ServerConfig.Json);
    using var document = JsonDocument.Parse(serialized);
    Require(document.RootElement.GetProperty("lobby").GetProperty("public").GetBoolean(), "Public flag was lost or serialized under the wrong name.");
    var roundTripPath = Path.Combine(root, "public-roundtrip.json");
    File.WriteAllText(roundTripPath, serialized);
    Require(ServerConfig.Load(roundTripPath).Lobby.Public, "Public flag was lost during round-trip.");
}));
await Check("CLI init refuses overwrite and produces a valid config", async () =>
{
    var path = Path.Combine(root, "cli-init", "server.json");
    Require((await Cli("init", "--config", path)).ExitCode == 0, "CLI init failed.");
    var original = File.ReadAllBytes(path);
    Require((await Cli("validate", "--config", path)).ExitCode == 0, "Initialized config did not validate.");
    var again = await Cli("init", "--config", path);
    Require(again.ExitCode == 1 && again.Error.Contains("not overwritten", StringComparison.Ordinal), "Second init did not explain refusal.");
    Require(original.SequenceEqual(File.ReadAllBytes(path)), "Existing config was overwritten.");
});
await Check("CLI rejects unknown duplicate and missing arguments before writing files", async () =>
{
    var path = Path.Combine(root, "invalid-cli.json");
    foreach (var arguments in new[]
    {
        new[] { "init", "--config", path, "--unknown", "value" },
        new[] { "init", "--config", path, "--config", path },
        new[] { "init", "--config" },
        new[] { "init", "--text", "unsupported" }
    })
    {
        var result = await Cli(arguments);
        Require(result.ExitCode == 1 && result.Error.Length > 0, "Invalid arguments were accepted.");
        Require(!File.Exists(path), "Invalid CLI invocation wrote a file.");
    }
});
await Check("no-graphics mode is rejected to protect paint and restoration saves", () => Sync(() =>
{
    var config = Config("graphics");
    Require(!config.Headless, "Default must retain the graphics context.");
    config.Validate();
    config.Headless = true;
    Throws<InvalidDataException>(() => config.Validate());
}));
await Check("chat templates reject unknown placeholders and oversized rendered warnings", () => Sync(() =>
{
    var config = Config("templates");
    config.Backups.StartMessage = new string('x', 256); config.Validate();
    config.Backups.StartMessage += "x"; Throws<InvalidDataException>(() => config.Validate());
    config.Backups.StartMessage = "Backup\nstarting"; Throws<InvalidDataException>(() => config.Validate());
    config.Backups.StartMessage = "Backup starting";
    config.Restarts.WarningMessage = "Restart {seconds}"; Throws<InvalidDataException>(() => config.Validate());
    config.Restarts.WarningMessage = "Restart {remaining"; Throws<InvalidDataException>(() => config.Validate());
    config.Restarts.WarningMessage = new string('x', 245) + "{remaining}";
    config.Restarts.WarningSeconds = [21599]; Throws<InvalidDataException>(() => config.Validate());
    config.Restarts.IntervalHours = 12; config.Restarts.WarningSeconds = [36000];
    // 600 minutes fits but a delayed 35999 seconds does not.
    Throws<InvalidDataException>(() => config.Validate());
    config.Restarts.WarningMessage = "Restart soon."; config.Validate();
    config.Restarts.StoppingMessage = null!; Throws<InvalidDataException>(() => config.Validate());
}));
await Check("warning renderer substitutes only remaining and enforces final chat limit", () => Sync(() =>
{
    Require(ChatMessages.RenderWarning("Maintenance in {remaining}.", 300) == "Maintenance in 5 minutes.", "Minutes were not substituted.");
    Require(ChatMessages.RenderWarning("{remaining}", 60) == "1 minute", "Singular minute was not rendered.");
    Require(ChatMessages.RenderWarning("{remaining}", 10) == "10 seconds", "Seconds were not substituted.");
    Require(ChatMessages.RenderWarning("{remaining}", 1) == "1 second", "Singular second was not rendered.");
    Require(ChatMessages.RenderWarning("Restart soon.", 10) == "Restart soon.", "Optional placeholder was required.");
    Throws<InvalidDataException>(() => ChatMessages.RenderWarning(new string('x', 245) + "{remaining}", 21599));
    Throws<InvalidDataException>(() => ChatMessages.RenderWarning("{remaining}", 0));
}));
await Check("relative config paths resolve against the config directory", () => Sync(() =>
{
    var path = Path.Combine(root, "relative", "server.json");
    SafeFiles.AtomicJson(path, new ServerConfig());
    var config = ServerConfig.Load(path);
    Require(config.DataDirectory == Path.GetFullPath(Path.Combine(root, "relative", "data")), "Relative directory resolved incorrectly.");
}));
await Check("runtime requires prepared copy marker and executable", () => Sync(() =>
{
    var config = Config("runtime");
    Directory.CreateDirectory(config.GameDirectory);
    File.WriteAllText(Path.Combine(config.GameDirectory, "Nomad Drive.exe"), "not executed");
    Throws<InvalidDataException>(() => config.Validate(true));
    File.WriteAllText(Path.Combine(config.GameDirectory, ".nomaddrive-server-runtime"), "test fixture");
    config.Validate(true);
}));
await Check("overlapping paths and sibling-prefix escapes are rejected", () => Sync(() =>
{
    var config = Config("overlap");
    config.DataDirectory = Path.Combine(config.GameDirectory, "data");
    Throws<InvalidDataException>(() => config.Validate());
    Require(!SafeFiles.Inside(Path.Combine(root, "run_escape", "file"), Path.Combine(root, "run")), "Sibling prefix accepted.");
}));
await Check("exclusive lock prevents concurrent import or supervisor", () => Sync(() =>
{
    var data = Path.Combine(root, "locked");
    using var held = SafeFiles.AcquireLock(data);
    Throws<IOException>(() => { using var another = SafeFiles.AcquireLock(data); });
}));
if (OperatingSystem.IsWindows())
{
    await Check("atomic status replacement survives a temporary Windows reader lock", async () =>
    {
        var directory = Path.Combine(root, "transient-status-lock");
        var path = Path.Combine(directory, "status.json");
        SafeFiles.AtomicJson(path, new { state = "old" });
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writing = Task.Run(() => { started.SetResult(); SafeFiles.AtomicJson(path, new { state = "new" }); });
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(200);
            Require(!writing.IsCompleted, "Replacement completed while delete sharing was denied.");
            Require(Protocol.Read<JsonElement>(path).GetProperty("state").GetString() == "old", "Old complete status changed while locked.");
        }
        finally { held.Dispose(); }
        await writing.WaitAsync(TimeSpan.FromSeconds(5));
        Require(Protocol.Read<JsonElement>(path).GetProperty("state").GetString() == "new", "Replacement did not recover after lock release.");
        Require(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "Successful replacement left a temporary file.");
    });
    await Check("permanent Windows replacement lock fails bounded without damaging status", () => Sync(() =>
    {
        var directory = Path.Combine(root, "permanent-status-lock");
        var path = Path.Combine(directory, "status.json");
        SafeFiles.AtomicJson(path, new { state = "old" });
        var before = File.ReadAllBytes(path);
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var watch = Stopwatch.StartNew();
        Exception? failure = null;
        try { SafeFiles.AtomicJson(path, new { state = "new" }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failure = ex; }
        Require(failure is not null, "A permanently locked destination unexpectedly succeeded.");
        Require(watch.Elapsed >= TimeSpan.FromMilliseconds(500) && watch.Elapsed < TimeSpan.FromSeconds(6), "Replacement retries were absent or unbounded.");
        Require(File.ReadAllBytes(path).SequenceEqual(before), "Failed replacement changed the existing record.");
        var pending = Directory.EnumerateFiles(directory, "*.tmp").Single();
        Require(Protocol.Read<JsonElement>(pending).GetProperty("state").GetString() == "new", "Flushed pending record was damaged.");
    }));
}

await Check("orphan runtime guard blocks another supervisor even after lock is released", () => Sync(() =>
{
    var directory = Path.Combine(root, "orphan");
    using var current = Process.GetCurrentProcess();
    SafeFiles.AtomicJson(Path.Combine(directory, "active-runtime.json"), new LiveRuntime
    {
        GamePid = current.Id, GameStartedUtc = current.StartTime.ToUniversalTime(), RunDirectory = "test fixture"
    });
    Throws<IOException>(() => RuntimeGuard.RejectLiveGame(directory));
}));
await Check("failed initial runtime record write terminates only the just-launched test child", () => Sync(() =>
{
    var directory = Path.Combine(root, "failed-registration");
    var record = Path.Combine(directory, "active-runtime.json");
    // A directory at the destination deterministically makes the record publication fail.
    Directory.CreateDirectory(record);
    var start = new ProcessStartInfo(Environment.ProcessPath!)
    {
        UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
    };
    if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(typeof(FakeGame).Assembly.Location);
    start.ArgumentList.Add("--registration-child");
    using var child = Process.Start(start) ?? throw new Exception("Could not start owned test process.");
    try
    {
        Throws<IOException>(() => StartupRegistration.Complete(child, () =>
        {
            SafeFiles.AtomicJson(record, new LiveRuntime { GamePid = child.Id, GameStartedUtc = child.StartTime.ToUniversalTime() });
            return true;
        }, TimeSpan.FromSeconds(5)));
        Require(child.HasExited, "Failed registration left the owned child running.");
        Require(!File.Exists(record), "Fixture unexpectedly published a runtime record.");
    }
    finally
    {
        // Failure cleanup is restricted to the exact test child created above.
        if (!child.HasExited) { child.Kill(entireProcessTree: true); child.WaitForExit(5000); }
    }
}));
await Check("import copies whole slot without overwriting source or selected world", () => Sync(() =>
{
    var source = Path.Combine(root, "source");
    Directory.CreateDirectory(Path.Combine(source, "backup"));
    File.WriteAllText(Path.Combine(source, "save.dat"), "save fixture");
    File.WriteAllText(Path.Combine(source, "backup", "previous.dat"), "backup fixture");
    var data = Path.Combine(root, "imported");
    var id = SafeFiles.ImportSlot(source, data);
    Require(SafeFiles.ReadWorld(data)?.SlotId == id, "Selected world not persisted.");
    Require(File.ReadAllText(Path.Combine(data, "saves", id, "backup", "previous.dat")) == "backup fixture", "Nested data missing.");
    File.WriteAllText(Path.Combine(data, "saves", id, "save.dat"), "server version");
    Require(File.ReadAllText(Path.Combine(source, "save.dat")) == "save fixture", "Source was modified.");
    Throws<IOException>(() => SafeFiles.ImportSlot(source, data));
}));
await Check("missing selected world and traversal slot IDs fail closed", () => Sync(() =>
{
    var data = Path.Combine(root, "invalidworld");
    SafeFiles.AtomicJson(Path.Combine(data, "world.json"), new WorldSelection { SlotId = "slot_missing" });
    Throws<InvalidDataException>(() => SafeFiles.ReadWorld(data));
    SafeFiles.AtomicJson(Path.Combine(data, "world.json"), new WorldSelection { SlotId = "../source" });
    Throws<InvalidDataException>(() => SafeFiles.ReadWorld(data));
}));
await Check("save failure does not approve stop or wait for process exit", async () =>
{
    var game = new FakeGame(root) { Reply = _ => new CommandResponse { Ok = false, Error = "Save write failed" } };
    var config = Config("savefailure"); config.Restarts.MessagesEnabled = false;
    var operations = new ServerOperations(game, config, _ => { });
    var response = await operations.ExecuteAsync("stop");
    Require(!response.Ok && !operations.ShutdownAcknowledged && game.ExitWaits == 0, "Failed save incorrectly approved shutdown.");
    Require(game.Commands.SequenceEqual(["shutdown"]), "Unexpected command sequence.");
});
await Check("timed out save blocks subsequent shutdown commands", async () =>
{
    var game = new FakeGame(root) { Reply = _ => throw new TimeoutException("Still saving") };
    var operations = new ServerOperations(game, Config("timeout"), _ => { });
    Require(!(await operations.ExecuteAsync("save")).Ok, "Timed out save reported success.");
    Require(!(await operations.ExecuteAsync("stop")).Ok && game.Commands.Count == 1, "Shutdown issued after unresolved save.");
});
await Check("successful shutdown waits for process exit before restart", async () =>
{
    var game = new FakeGame(root);
    var config = Config("restart"); config.Restarts.MessagesEnabled = false;
    var operations = new ServerOperations(game, config, _ => { });
    Require((await operations.ExecuteAsync("restart")).Ok, "Successful restart failed.");
    Require(operations.ShutdownAcknowledged && game.ExitWaits == 1, "Did not wait for game exit.");
});
await Check("late exit after shutdown timeout never authorizes crash restart", async () =>
{
    foreach (var command in new[] { "stop", "restart" })
    {
        var config = Config("late-" + command); config.Restarts.MessagesEnabled = false;
        var game = new FakeGame(root) { Reply = _ => throw new TimeoutException("Parking is still running") };
        var operations = new ServerOperations(game, config, _ => { });
        Require(!(await operations.ExecuteAsync(command)).Ok, "Timed out shutdown reported success.");
        Require(operations.RequiresManualExitReview && !operations.ShutdownAcknowledged, "An uncertain shutdown could be restarted as a crash.");
        Require(game.ExitWaits == 0 && !(await operations.ExecuteAsync("save")).Ok, "Uncertain shutdown admitted more operations.");
    }
});
await Check("unreadable shutdown acknowledgement requires manual exit review", async () =>
{
    var config = Config("unreadable-shutdown"); config.Restarts.MessagesEnabled = false;
    var game = new FakeGame(root) { Reply = _ => throw new InvalidDataException("Invalid response ID") };
    var operations = new ServerOperations(game, config, _ => { });
    Require(!(await operations.ExecuteAsync("stop")).Ok && operations.RequiresManualExitReview, "Unconfirmed shutdown was treated as an ordinary crash.");
});
await Check("acknowledged shutdown with hung process does not claim restart success", async () =>
{
    var game = new FakeGame(root) { ExitResult = false };
    var config = Config("hung"); config.Restarts.MessagesEnabled = false;
    var operations = new ServerOperations(game, config, _ => { });
    Require(!(await operations.ExecuteAsync("restart")).Ok && operations.ShutdownAcknowledged, "Hung game was treated as stopped.");
    Require(!(await operations.ExecuteAsync("save")).Ok && game.Commands.Count == 1, "Issued command after shutdown acknowledgement.");
});
await Check("failed snapshot produces no backup archive", async () =>
{
    var game = new FakeGame(root) { Reply = _ => new CommandResponse { Ok = false, Error = "Save failed" } };
    var config = Config("failedbackup");
    var result = await new ServerOperations(game, config, _ => { }).ExecuteAsync("backup");
    Require(!result.Ok && !Directory.Exists(Path.Combine(config.DataDirectory, "backups")), "Archived a failed snapshot.");
});
await Check("backup operation sends configured start and completion messages", async () =>
{
    var run = Path.Combine(root, "custom-notice-run");
    var snapshot = Path.Combine(run, "snapshots", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(snapshot);
    File.WriteAllText(Path.Combine(snapshot, "save.dat"), "saved data");
    var game = new FakeGame(run) { Reply = command => new CommandResponse { Ok = true, SnapshotPath = command == "snapshot" ? snapshot : "" } };
    var config = Config("custombackupnotice");
    config.Backups.MessagesEnabled = true;
    config.Backups.StartMessage = "Custom backup starting";
    config.Backups.CompletedMessage = "Custom backup finished";
    var response = await new ServerOperations(game, config, _ => { }).ExecuteAsync("backup");
    Require(response.Ok, "Backup failed: " + response.Error);
    Require(game.Commands.SequenceEqual(["say", "snapshot", "say"]), "Backup message order incorrect.");
    Require(game.Texts.SequenceEqual(["Custom backup starting", "", "Custom backup finished"]), "Configured backup messages were not used.");
});
await Check("shutdown operations send configured restart and stopping messages", async () =>
{
    var config = Config("customshutdownnotice");
    config.Restarts.RestartingMessage = "Custom restart notice";
    config.Restarts.StoppingMessage = "Custom stop notice";
    foreach (var command in new[] { "restart", "stop" })
    {
        var game = new FakeGame(root);
        Require((await new ServerOperations(game, config, _ => { }).ExecuteAsync(command)).Ok, "Shutdown operation failed.");
        Require(game.Commands.SequenceEqual(["say", "shutdown"]), "Shutdown message order incorrect.");
        Require(game.Texts[0] == (command == "restart" ? "Custom restart notice" : "Custom stop notice"), "Configured shutdown message was not used.");
    }
});
await Check("snapshot path outside launch directory is rejected", () => Sync(() =>
{
    var store = new BackupStore(Path.Combine(root, "escaped-backups"), 2);
    Throws<InvalidDataException>(() => store.Create(Path.Combine(root, "source"), Path.Combine(root, "run"), DateTimeOffset.UtcNow));
}));
await Check("backup preserves nested bytes and retention preserves unrelated archives", () => Sync(() =>
{
    var run = Path.Combine(root, "backuprun");
    string MakeSnapshot()
    {
        var path = Path.Combine(run, "snapshots", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(path, "backup"));
        File.WriteAllBytes(Path.Combine(path, "save.dat"), [1, 2, 3, 4]);
        File.WriteAllText(Path.Combine(path, "backup", "old.dat"), "old");
        return path;
    }
    var destination = Path.Combine(root, "archive");
    Directory.CreateDirectory(destination);
    File.WriteAllText(Path.Combine(destination, "personal.zip"), "unrelated");
    File.WriteAllText(Path.Combine(destination, "nomaddrive-not-ours.zip"), "unrelated");
    var store = new BackupStore(destination, 2);
    var now = DateTimeOffset.Parse("2026-11-12T12:00:00Z");
    var firstSnapshot = MakeSnapshot();
    store.Create(firstSnapshot, run, now);
    Require(!Directory.Exists(firstSnapshot), "Successfully archived snapshot was not removed.");
    store.Create(MakeSnapshot(), run, now.AddMinutes(1));
    var newest = store.Create(MakeSnapshot(), run, now.AddMinutes(2));
    Require(Directory.GetFiles(destination, "*.zip").Length == 4, "Retention deleted wrong number of files.");
    Require(File.Exists(Path.Combine(destination, "personal.zip")), "Deleted unrelated archive.");
    using var archive = ZipFile.OpenRead(newest);
    using var stream = archive.GetEntry("save.dat")!.Open();
    using var memory = new MemoryStream(); stream.CopyTo(memory);
    Require(memory.ToArray().SequenceEqual(new byte[] { 1, 2, 3, 4 }), "Backup changed save bytes.");
    Require(archive.GetEntry("backup/old.dat") != null, "Backup lost nested file.");
}));
await Check("crash retries are bounded across short successful sessions", () => Sync(() =>
{
    var clock = new FakeClock();
    var config = new RecoveryConfig { MaxAttempts = 3, WindowMinutes = 30 };
    var budget = new CrashBudget(config, clock);
    Require(budget.TrySpend() && budget.TrySpend() && budget.TrySpend() && !budget.TrySpend(), "Crash retry budget was not bounded.");
    clock.Advance(TimeSpan.FromMinutes(30));
    Require(budget.TrySpend(), "Retry window did not expire.");
    config.Enabled = false; Require(!budget.TrySpend(), "Disabled recovery spent retry.");
}));
await Check("backup and restart schedules fire once with warning thresholds", () => Sync(() =>
{
    var clock = new FakeClock(); var config = Config("schedule");
    var schedule = new MaintenanceSchedule(config, clock); schedule.Begin();
    Require(!schedule.Poll().Backup, "Backup fired immediately.");
    clock.Advance(TimeSpan.FromMinutes(30)); Require(schedule.Poll().Backup && !schedule.Poll().Backup, "Backup scheduling repeated or failed.");
    clock.Advance(TimeSpan.FromMinutes(320));
    Require(schedule.Poll().Warnings.SequenceEqual([600]), "10-minute warning missing.");
    Require(schedule.Poll().Warnings.Length == 0, "Warning repeated.");
    clock.Advance(TimeSpan.FromMinutes(9)); Require(schedule.Poll().Warnings.SequenceEqual([60]), "Missed thresholds did not collapse to urgent warning.");
    clock.Advance(TimeSpan.FromMinutes(1)); Require(schedule.Poll().Restart && !schedule.Poll().Restart, "Restart repeated or missing.");
}));
await Check("delayed restart warning reports actual remaining time rather than crossed threshold", () => Sync(() =>
{
    var clock = new FakeClock(); var config = Config("delayedwarning");
    var schedule = new MaintenanceSchedule(config, clock); schedule.Begin();
    clock.Advance(TimeSpan.FromMinutes(350));
    Require(schedule.Poll().Warnings.SequenceEqual([600]), "Initial threshold did not fire.");
    clock.Advance(TimeSpan.FromMinutes(8));
    var warning = schedule.Poll();
    Require(warning.Warnings.SequenceEqual([120]), "Delayed five-minute threshold did not report the two minutes actually remaining.");
    Require(ChatMessages.RenderWarning(config.Restarts.WarningMessage, warning.Warnings[0]) == "Server restart in 2 minutes.", "Rendered countdown was stale.");
    Require(schedule.Poll().Warnings.Length == 0, "Crossed threshold was emitted twice.");
}));
await Check("multiple crossed warning thresholds collapse to one current rounded-up countdown", () => Sync(() =>
{
    var clock = new FakeClock(); var config = Config("crossedwarnings");
    var schedule = new MaintenanceSchedule(config, clock); schedule.Begin();
    clock.Advance(TimeSpan.FromHours(6) - TimeSpan.FromMilliseconds(3200));
    var due = schedule.Poll();
    Require(due.Warnings.SequenceEqual([4]) && !due.Restart, "Several crossed thresholds did not collapse to actual rounded-up seconds.");
    Require(schedule.Poll().Warnings.Length == 0, "A stale warning burst followed the collapsed countdown.");
}));
await Check("warnings never fire before threshold or announce zero before the deadline", () => Sync(() =>
{
    var clock = new FakeClock(); var config = Config("warningboundary");
    config.Restarts.WarningSeconds = [10];
    var schedule = new MaintenanceSchedule(config, clock); schedule.Begin();
    clock.Advance(TimeSpan.FromHours(6) - TimeSpan.FromMilliseconds(10100));
    Require(schedule.Poll().Warnings.Length == 0, "Warning fired before its threshold.");
    clock.Advance(TimeSpan.FromSeconds(10));
    var due = schedule.Poll();
    Require(due.Warnings.SequenceEqual([1]) && !due.Restart, "Fractional positive countdown must round up to one second.");
    clock.Advance(TimeSpan.FromMilliseconds(100));
    due = schedule.Poll();
    Require(due.Restart && due.Warnings.Length == 0, "Deadline should restart without a zero-second warning.");
}));
await Check("disabled maintenance and disabled chat do not emit operations or warnings", () => Sync(() =>
{
    var clock = new FakeClock(); var config = Config("disabledmaintenance");
    config.Backups.Enabled = false; config.Restarts.Enabled = false;
    var schedule = new MaintenanceSchedule(config, clock); schedule.Begin();
    clock.Advance(TimeSpan.FromHours(7));
    var due = schedule.Poll();
    Require(!due.Backup && !due.Restart && due.Warnings.Length == 0, "Disabled maintenance ran.");
    config.Restarts.Enabled = true; config.Restarts.MessagesEnabled = false; schedule.Begin();
    clock.Advance(TimeSpan.FromMinutes(355));
    Require(schedule.Poll().Warnings.Length == 0, "Disabled restart chat emitted a warning.");
    clock.Advance(TimeSpan.FromMinutes(5));
    due = schedule.Poll();
    Require(due.Restart && due.Warnings.Length == 0, "Disabling chat suppressed the actual restart or emitted a warning.");
}));
await Check("overdue schedule restarts once without stale countdown and resets next deadline", () => Sync(() =>
{
    var clock = new FakeClock(); var config = Config("overdueschedule");
    var schedule = new MaintenanceSchedule(config, clock); schedule.Begin();
    clock.Advance(TimeSpan.FromHours(7));
    var due = schedule.Poll();
    Require(due.Restart && due.Warnings.Length == 0, "Overdue restart emitted stale countdown warnings.");
    due = schedule.Poll();
    Require(!due.Restart && due.Warnings.Length == 0, "Overdue restart repeated immediately.");
    clock.Advance(TimeSpan.FromMinutes(350));
    Require(schedule.Poll().Warnings.SequenceEqual([600]), "Next cycle did not start from the rescheduled deadline.");
}));
await Check("IPC waits for matching atomic response", async () =>
{
    var directory = Path.Combine(root, "ipc");
    var request = new CommandRequest { Command = "save" };
    var pending = Protocol.SendAsync(directory, request, TimeSpan.FromSeconds(5));
    var written = Protocol.Read<CommandRequest>(Path.Combine(directory, "requests", request.Id + ".json"));
    Require(written?.Command == "save", "Request not written.");
    SafeFiles.AtomicJson(Path.Combine(directory, "responses", request.Id + ".json"), new CommandResponse { Id = request.Id, Ok = true });
    Require((await pending).Ok, "Response not delivered.");
});
await Check("host error and stale heartbeat suspend scheduled work", () => Sync(() =>
{
    var now = DateTimeOffset.UtcNow;
    var grace = TimeSpan.FromSeconds(120);
    var status = new PluginStatus { State = "ready", UpdatedUtc = now };
    Require(HostHealth.Problem(status, now, grace) is null, "Healthy host was marked unhealthy.");
    status.State = "error"; status.Detail = "Lobby disconnected";
    Require(HostHealth.Problem(status, now, grace)?.Contains("Lobby disconnected") == true, "Host error was not surfaced.");
    status.State = "ready"; status.UpdatedUtc = now.AddSeconds(-121);
    Require(HostHealth.Problem(status, now, grace)?.Contains("stale") == true, "Stale heartbeat was not detected.");
    Require(HostHealth.Problem(null, now, grace) is not null, "Missing heartbeat was not detected.");
}));
await Check("offline recovery pins the selected completed checkpoint through process exit", async () =>
{
    var data = Path.Combine(root, "terminal-recovery");
    const string slotId = "slot_recovery";
    var slot = Path.Combine(data, "saves", slotId);
    Directory.CreateDirectory(slot);
    SafeFiles.AtomicJson(Path.Combine(data, "world.json"), new WorldSelection { SlotId = slotId });
    var bytes = "completed world"u8.ToArray();
    var save = Path.Combine(slot, "save.dat");
    var meta = Path.Combine(slot, "meta.json");
    File.WriteAllBytes(save, bytes);
    SafeFiles.AtomicJson(meta, new { slotId, contentFile = "save.dat", contentSize = bytes.Length,
        contentHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant() });
    var calls = 0;
    Task Exit()
    {
        calls++;
        if (OperatingSystem.IsWindows())
        {
            Throws<IOException>(() => File.OpenWrite(save).Dispose());
            Throws<IOException>(() => File.OpenWrite(meta).Dispose());
        }
        return Task.CompletedTask;
    }
    await SafeFiles.WithVerifiedCheckpointAsync(data, slotId, Exit);
    async Task Refused(string reported)
    {
        try { await SafeFiles.WithVerifiedCheckpointAsync(data, reported, Exit); }
        catch (InvalidDataException) { return; }
        throw new Exception("Unverified checkpoint authorized termination.");
    }
    await Refused("slot_other");
    Array.Reverse(bytes);
    File.WriteAllBytes(save, bytes);
    await Refused(slotId);
    File.WriteAllBytes(save, []);
    await Refused(slotId);
    File.WriteAllBytes(save, bytes);
    SafeFiles.AtomicJson(meta, new { slotId, contentFile = "../other.dat", contentSize = bytes.Length,
        contentHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)) });
    await Refused(slotId);
    Require(calls == 1, "Invalid save metadata reached the termination callback.");
});
await Check("IPC rejects mismatched response IDs", async () =>
{
    var directory = Path.Combine(root, "badipc");
    var request = new CommandRequest { Command = "save" };
    SafeFiles.AtomicJson(Path.Combine(directory, "responses", request.Id + ".json"), new CommandResponse { Id = Guid.NewGuid().ToString("N"), Ok = true });
    try { await Protocol.SendAsync(directory, request, TimeSpan.FromSeconds(1)); }
    catch (InvalidDataException) { return; }
    throw new Exception("Accepted wrong response ID.");
});
await Check("concurrent IPC reads tolerate 500 atomic File.Replace updates", async () =>
{
    var directory = Path.Combine(root, "replace-ipc");
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, "status.json");
    var payload = new string('x', 32768);
    File.WriteAllText(path, JsonSerializer.Serialize(new PluginStatus { State = "ready", Players = 0, Detail = "0:" + payload }, Protocol.Json));
    using var start = new ManualResetEventSlim();
    int reads = 0;
    var writer = Task.Run(() =>
    {
        start.Wait();
        for (var index = 1; index <= 500; index++)
        {
            var temporary = Path.Combine(directory, "replacement.json");
            File.WriteAllText(temporary, JsonSerializer.Serialize(new PluginStatus { State = "ready", Players = index, Detail = index + ":" + payload }, Protocol.Json));
            File.Replace(temporary, path, null);
        }
    });
    var reader = Task.Run(() =>
    {
        start.Wait();
        while (!writer.IsCompleted || reads < 500)
        {
            var status = Protocol.Read<PluginStatus>(path, tolerateSharingViolation: true);
            if (status is null) continue;
            Require(status.State == "ready" && status.Detail == status.Players + ":" + payload, "Read a partial or mixed JSON generation.");
            reads++;
        }
    });
    start.Set();
    await Task.WhenAll(writer, reader);
    Require(Protocol.Read<PluginStatus>(path)?.Players == 500 && reads >= 500, "Final replacement or concurrent reads missing.");
});
await Check("sharing violation polling is bounded while startup guards fail closed", () => Sync(() =>
{
    var path = Path.Combine(root, "sharing.json");
    File.WriteAllText(path, "{\"state\":\"ready\"}");
    using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    var watch = Stopwatch.StartNew();
    Require(Protocol.Read<PluginStatus>(path, tolerateSharingViolation: true) is null, "Locked polling read was not deferred.");
    Require(watch.Elapsed < TimeSpan.FromSeconds(2), "Polling read blocked too long.");
    Throws<IOException>(() => Protocol.Read<PluginStatus>(path));
}));

Console.WriteLine($"{passed} passed; {failed} failed.");
return failed == 0 ? 0 : 1;

sealed record TestConnection(int Id);

sealed class FakeClock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.Parse("2026-11-12T12:00:00Z");
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan time) => _now += time;
}
sealed class FakeGame(string run) : IGameSession
{
    public string RunDirectory { get; } = run;
    public bool HasExited => false;
    public bool ExitResult { get; set; } = true;
    public int ExitWaits { get; private set; }
    public List<string> Commands { get; } = new();
    public List<string> Texts { get; } = new();
    public Func<string, CommandResponse> Reply { get; set; } = _ => new CommandResponse { Ok = true };
    public Task<CommandResponse> SendAsync(string command, string text = "")
    {
        Commands.Add(command); Texts.Add(text); return Task.FromResult(Reply(command));
    }
    public Task<bool> WaitForExitAsync(TimeSpan timeout) { ExitWaits++; return Task.FromResult(ExitResult); }
}
