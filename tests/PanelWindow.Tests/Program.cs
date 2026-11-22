using NomadDrive.Panel;
using NomadDrive.Server;
using System.Text.Json;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        var dir = Directory.CreateDirectory(Path.Combine(args.Single(),Guid.NewGuid().ToString("N"))).FullName;
        var path = Path.Combine(dir,"server.json");
        var config = new ServerConfig();
        config.Lobby.Name = "Test lobby";
        File.WriteAllText(path,JsonSerializer.Serialize(config,ServerConfig.Json));
        var profiles = new PanelProfiles(dir,_ => throw new Exception("Unexpected sign-in registration"));
        profiles.Add("Test server",path);
        Application.EnableVisualStyles();
        using var signal = new EventWaitHandle(false,EventResetMode.AutoReset);
        using var signIn = new EventWaitHandle(false,EventResetMode.AutoReset);
        using var context = new PanelContext(profiles,signal,signIn,false);
        var form = Application.OpenForms.OfType<PanelWindow>().Single();
        var tabs = Find<TabControl>(form).Single();
        Wait(() => Find<Button>(form).Single(b => b.Text=="Start").Enabled);
        Check(form.Text == "Test lobby (0/4, stopped)", "Stopped title uses the configured server name and capacity");
        Check(tabs.TabPages.Count==4 && form.Visible,"Server, Mods, Settings and Logs open on a stopped profile");
        using (var image = new Bitmap(form.Width,form.Height))
        {
            form.DrawToBitmap(image,new Rectangle(Point.Empty,form.Size));
            image.Save(Path.Combine(dir,"panel.png"));
        }
        Check(!Find<Button>(form).Single(b=>b.Text=="Stop").Enabled,"Stopped server cannot be stopped again");
        form.Close(); Application.DoEvents();
        Check(!form.IsDisposed && !form.Visible,"Window close hides to the tray");
        signal.Set(); Wait(()=>form.Visible);
        Check(Application.OpenForms.OfType<PanelWindow>().Count()==1,"Open signal restores the existing window");
        var resolved = ServerConfig.Load(path);
        var run = Directory.CreateDirectory(Path.Combine(resolved.DataDirectory,"runs","fixture")).FullName;
        SafeFiles.AtomicJson(Path.Combine(resolved.DataDirectory,"supervisor.json"),
            new SupervisorStatus { State = "ready", RunDirectory = run });
        var heartbeat = Path.Combine(run,"status.json");
        var game = new PluginStatus { State = "ready", Players = 2, MaxPlayers = 15, UpdatedUtc = DateTimeOffset.UtcNow };
        SafeFiles.AtomicJson(heartbeat,game);
        Wait(() => form.Text == "Test lobby (2/15)");
        form.Close();
        game.Players = 3;
        SafeFiles.AtomicJson(heartbeat,game);
        Wait(() => context.TrayTooltip == "Test lobby (3/15)");
        Check(!form.Visible && form.Text == "Test lobby (3/15)", "Tray tooltip and title refresh while hidden");
        config.Lobby.Name = new string('X',64);
        SafeFiles.AtomicJson(path,config);
        Wait(() => context.TrayTooltip.StartsWith("XXXX"));
        Check(context.TrayTooltip.Length <= 63 && context.TrayTooltip.EndsWith(" (3/15)"), "Long tray tooltip preserves player count");
        config.Lobby.Name = "Test lobby";
        SafeFiles.AtomicJson(path,config);
        game.UpdatedUtc = DateTimeOffset.UtcNow.AddHours(-1);
        SafeFiles.AtomicJson(heartbeat,game);
        Wait(() => form.Text == "Test lobby (?/4)");
        Check(form.Text == "Test lobby (?/4)", "Stale occupancy shows as unknown");
        using (var settings = new SettingsWindow(path))
        {
            settings.Show(); Application.DoEvents();
            Check(Find<TabControl>(settings).Single().TabPages.Count==6,"Every settings section opens");
            Check(Find<TextBox>(settings).Single(t=>t.AccessibleName=="Password").UseSystemPasswordChar,"Password is masked");
            settings.Close();
        }
        var original=File.ReadAllBytes(path);
        config.Lobby.Name="Edited profile";
        SettingsWindow.SaveConfig(path,original,config);
        Check(ServerConfig.Load(path).Lobby.Name=="Edited profile","Edited config validates and saves");
        try { SettingsWindow.SaveConfig(path,original,config); throw new Exception("Concurrent change was overwritten"); }
        catch(IOException) { }
        var current=File.ReadAllBytes(path);
        config.Lobby.MaxPlayers=0;
        try { SettingsWindow.SaveConfig(path,current,config); throw new Exception("Invalid config was saved"); }
        catch(InvalidDataException) { }
        Check(current.SequenceEqual(File.ReadAllBytes(path)),"Invalid settings preserve prior config");
    }
    static IEnumerable<T> Find<T>(Control control) where T:Control =>
        control.Controls.Cast<Control>().SelectMany(c => Find<T>(c).Prepend(c).OfType<T>());
    static void Check(bool value,string name) { if(!value)throw new Exception(name);Console.WriteLine("PASS "+name); }
    static void Wait(Func<bool> predicate)
    {
        var until=DateTime.UtcNow.AddSeconds(10);
        while(!predicate()) { if(DateTime.UtcNow>until)throw new TimeoutException(); Application.DoEvents();Thread.Sleep(25); }
    }
}
