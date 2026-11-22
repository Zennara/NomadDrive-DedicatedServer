using NomadDrive.Panel;
using Microsoft.Win32;

if (args.Length != 1) throw new ArgumentException("Pass an isolated fixture directory.");
var directory = Directory.CreateDirectory(Path.Combine(Path.GetFullPath(args[0]), Guid.NewGuid().ToString("N"))).FullName;
var config = Path.Combine(directory, "test server.json");
File.WriteAllText(config, "{}");
var registered = new List<bool>();
var panel = new PanelProfiles(directory, registered.Add);
var staleWindow = new PanelProfiles(directory, registered.Add);
Require(panel.IsFirstRun);
var profile = panel.Add("First server", config);
Require(!profile.AutoStart && !panel.StartPanelAtSignIn && registered.Count == 0);
ExpectFailure<InvalidOperationException>(() => panel.Add("Duplicate", config.ToUpperInvariant()));
staleWindow.SetPanelStartup(true);
Require(staleWindow.Profiles.Count == 1);
panel.SetAutoStart(config, true);
panel.SetPanelStartup(false);
Require(panel.Profiles.Single().AutoStart && !panel.StartPanelAtSignIn && registered.SequenceEqual([true,true,true]));
var denied = new PanelProfiles(directory, _ => throw new UnauthorizedAccessException());
ExpectFailure<UnauthorizedAccessException>(() => denied.SetAutoStart(config, false));
var restored = new PanelProfiles(directory, registered.Add);
Require(restored.Profiles.Single().AutoStart);
restored.Remove(config);
Require(registered.SequenceEqual([true,true,true,false]) && File.Exists(config) && restored.Profiles.Count == 0 && !restored.IsFirstRun);
File.WriteAllText(Path.Combine(directory,"profiles.json"), """{"version":12,"startPanelAtSignIn":true,"profiles":[]}""");
ExpectFailure<InvalidDataException>(() => new PanelProfiles(directory, registered.Add));
var keyPath = @"Software\NomadDrive-Panel-Test-" + Guid.NewGuid().ToString("N");
try
{
    var executable = Path.Combine(Directory.CreateDirectory(Path.Combine(directory, "First install")).FullName, "NomadDrive.Panel.exe");
    var moved = Path.Combine(Directory.CreateDirectory(Path.Combine(directory, "New install")).FullName, "NomadDrive.Panel.exe");
    File.WriteAllText(executable, "");
    File.WriteAllText(moved, "");
    var settingsPath = Path.Combine(directory, "portable-settings");
    var portable = new PanelProfiles(settingsPath, enabled => PanelProfiles.RegisterAtSignIn(enabled, keyPath, executable));
    portable.SetPanelStartup(true);
    using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true)!;
    Require((string?)key.GetValue("NomadDriveDedicatedServerPanel") == $"\"{executable}\" --startup");
    key.SetValue("Unrelated", "keep");
    var relocated = new PanelProfiles(settingsPath, enabled => PanelProfiles.RegisterAtSignIn(enabled, keyPath, moved), interactive: true);
    Require(relocated.StartupWarning == null && (string?)key.GetValue("NomadDriveDedicatedServerPanel") == $"\"{moved}\" --startup");
    key.DeleteValue("NomadDriveDedicatedServerPanel");
    relocated.SetPanelStartup(true);
    Require((string?)key.GetValue("NomadDriveDedicatedServerPanel") == $"\"{moved}\" --startup");
    key.SetValue("NomadDriveDedicatedServerPanel", "unrelated.exe");
    var conflicted = new PanelProfiles(settingsPath, enabled => PanelProfiles.RegisterAtSignIn(enabled, keyPath, moved), interactive: true);
    Require(conflicted.StartupWarning != null && conflicted.StartPanelAtSignIn);
    conflicted.SetPanelStartup(false);
    Require((string?)key.GetValue("NomadDriveDedicatedServerPanel") == "unrelated.exe");
    key.DeleteValue("NomadDriveDedicatedServerPanel");
    relocated.SetPanelStartup(true);
    relocated.SetPanelStartup(false);
    Require(key.GetValue("NomadDriveDedicatedServerPanel") is null && (string?)key.GetValue("Unrelated") == "keep");
}
finally { Registry.CurrentUser.DeleteSubKey(keyPath, throwOnMissingSubKey: false); }
Console.WriteLine("Profile defaults, duplicate paths, concurrent edits, independent sign-in choices, registration rollback, removal and version checks passed.");

static void Require(bool value)
{
    if (!value) throw new InvalidOperationException("Profile assertion failed.");
}

static void ExpectFailure<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}
