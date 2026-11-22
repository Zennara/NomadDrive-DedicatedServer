using System.Security.Principal;

namespace NomadDrive.Panel;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var startup = args.Length == 1 && args[0] == "--startup";
        if (args.Length != 0 && !startup)
        {
            MessageBox.Show("Launch without arguments to open the panel.", "Nomad Drive");
            return;
        }
        var id = WindowsIdentity.GetCurrent().User!.Value;
        using var mutex = new Mutex(true, @"Local\NomadDrive.Panel." + id, out var first);
        using var activate = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\NomadDrive.Panel.Show." + id);
        using var signIn = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\NomadDrive.Panel.Startup." + id);
        if (!first)
        {
            (startup ? signIn : activate).Set();
            return;
        }
        try
        {
            var profiles = PanelProfiles.Load(interactive: !startup);
            using var context = new PanelContext(profiles, activate, signIn, startup);
            if (profiles.StartupWarning != null) MessageBox.Show(profiles.StartupWarning,"Panel sign-in settings");
            Application.Run(context);
        }
        catch (Exception e) { MessageBox.Show(e.Message, "Nomad Drive panel", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}

internal sealed class PanelContext : ApplicationContext
{
    private readonly PanelWindow window;
    private readonly NotifyIcon tray;
    private readonly System.Windows.Forms.Timer activation = new() { Interval = 300 };
    private readonly ContextMenuStrip menu = new();
    private bool quitting;
    private bool startingProfiles;

    internal PanelContext(PanelProfiles profiles, EventWaitHandle signal, EventWaitHandle signIn, bool startup)
    {
        window = new PanelWindow(profiles);
        tray = new NotifyIcon { Text = "Nomad Drive server panel", Icon = SystemIcons.Application, ContextMenuStrip = menu, Visible = true };
        menu.Items.Add("Open server panel", null, (_, _) => Open());
        menu.Items.Add("Quit panel", null, (_, _) => Quit());
        tray.DoubleClick += (_, _) => Open();
        window.FormClosing += (_, e) =>
        {
            if (!quitting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; window.Hide(); }
            else if (!quitting) ExitThread();
        };
        activation.Tick += async (_, _) =>
        {
            if (signal.WaitOne(0)) Open();
            if (signIn.WaitOne(0)) await StartProfiles(profiles, false);
        };
        activation.Start();
        if (startup)
        {
            async void OnIdle(object? sender, EventArgs args)
            {
                Application.Idle -= OnIdle;
                await StartProfiles(profiles, true);
            }
            Application.Idle += OnIdle;
        }
        else Open();
    }

    private void Open()
    {
        window.Show();
        if (window.WindowState == FormWindowState.Minimized) window.WindowState = FormWindowState.Normal;
        window.Activate();
    }

    private async Task StartProfiles(PanelProfiles profiles, bool exitWhenHidden)
    {
        if (startingProfiles || quitting) return;
        startingProfiles = true;
        var errors = new List<string>();
        try
        {
            foreach (var profile in profiles.Profiles.Where(p => p.AutoStart))
            {
                try { await new PanelBackend(profile.ConfigPath).StartAsync(); }
                catch (Exception e) { errors.Add(profile.Name + ": " + e.Message); }
            }
        }
        finally { startingProfiles = false; }
        if (quitting) return;
        if (errors.Count > 0)
        {
            Open();
            MessageBox.Show(window, string.Join(Environment.NewLine, errors), "Server startup");
        }
        else if (exitWhenHidden && !profiles.StartPanelAtSignIn && !window.Visible) Quit();
    }

    private void Quit()
    {
        if (window.OperationPending || startingProfiles)
        {
            MessageBox.Show(window, "A server operation is still in progress. You can close the window to the tray while it finishes.", "Server panel");
            return;
        }
        quitting = true;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            quitting = true;
            activation.Dispose(); tray.Visible = false; tray.Dispose(); menu.Dispose(); window.Dispose();
        }
        base.Dispose(disposing);
    }
}
