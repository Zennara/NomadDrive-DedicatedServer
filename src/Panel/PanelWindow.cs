using NomadDrive.Server;

namespace NomadDrive.Panel;

internal sealed class PanelWindow : Form
{
    private readonly PanelProfiles profiles;
    private readonly ComboBox selector = Choice();
    private readonly Label status = new() { AutoSize = true, Padding = new(4, 10, 4, 8) };
    private readonly Label feedback = new() { Dock = DockStyle.Bottom, Height = 42, Padding = new(12, 4, 12, 4), AutoEllipsis = true };
    private readonly ListBox roster = new() { Dock = DockStyle.Fill };
    private readonly ListBox mods = new() { Dock = DockStyle.Fill };
    private readonly TextBox announcement = new() { Width = 300, PlaceholderText = "Message to all online players", MaxLength = 256 };
    private readonly ComboBox logKind = Choice();
    private readonly TextBox logs = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill };
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    private readonly CheckBox autoStart = new() { AutoSize = true, Text = "Start this server at Windows sign-in" };
    private readonly CheckBox autoPanel = new() { AutoSize = true, Text = "Open panel in the tray at sign-in" };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 4000 };
    private readonly Dictionary<string, Button> actions = new();
    private readonly Dictionary<string, string> outcomes = new(StringComparer.OrdinalIgnoreCase);
    private PanelProfile? Selected => selector.SelectedItem as PanelProfile;
    private PanelBackend? backend;
    private PanelStatus? snapshot;
    private bool refreshing, binding;
    internal bool OperationPending { get; private set; }

    internal PanelWindow(PanelProfiles profiles)
    {
        this.profiles = profiles;
        Icon = Program.PanelIcon;
        Size = new(940, 620); MinimumSize = new(780, 510);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10);
        var header = Row();
        selector.Width = 265; selector.DropDownWidth = 560; selector.AccessibleName = "Server profile";
        selector.FormattingEnabled = true;
        selector.Format += (_, e) =>
        {
            if (e.ListItem is PanelProfile p)
                e.Value = profiles.Profiles.Count(other => other.Name.Equals(p.Name,StringComparison.OrdinalIgnoreCase)) > 1
                    ? $"{p.Name} ({p.ConfigPath})" : p.Name;
        };
        header.Controls.Add(new PictureBox { Image = Program.PanelLogo, Size = new(32, 32), SizeMode = PictureBoxSizeMode.Zoom });
        header.Controls.Add(new Label { Text = "Server profile", ForeColor = Color.White, AutoSize = true, Padding = new(0, 7, 6, 0) });
        header.Controls.Add(selector);
        Add(header, "Open config", () => PickProfile(false));
        Add(header, "New config", () => PickProfile(true));
        Add(header, "Forget profile", Forget);
        header.Padding = new(12,9,12,9);
        header.BackColor = Color.FromArgb(35,52,65);
        var server = Page("Server");
        var controls = Row();
        foreach (var label in new[] { "Start", "Save", "Backup", "Restart", "Stop" })
            Add(controls, label, () => Command(label));
        Add(controls, "Edit config", Edit);
        var chat = Row(); chat.Controls.Add(announcement); Add(chat, "Announce", () => Command("Announce"));
        var online = new GroupBox { Text = "Online players", Dock = DockStyle.Fill, Padding = new(10) };
        online.Controls.Add(roster);
        server.Controls.Add(online); server.Controls.Add(chat); server.Controls.Add(controls); server.Controls.Add(status);
        status.Dock = DockStyle.Top;
        var plugins = Page("Mods"); var modButtons = Row();
        Add(modButtons, "Install mod", Install); Add(modButtons, "Enable", () => Toggle(true)); Add(modButtons, "Disable", () => Toggle(false));
        plugins.Controls.Add(mods); plugins.Controls.Add(modButtons);
        var settings = Page("Settings"); var options = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        Add(options, "Edit server settings", Edit);
        Add(options, "Import saved world", ImportWorld);
        options.Controls.Add(autoStart); options.Controls.Add(autoPanel);
        settings.Controls.Add(options);
        var logPage = Page("Logs"); var source = Row(); logKind.Items.AddRange(["Supervisor", "Game", "Plugins"]); logKind.SelectedIndex = 0;
        source.Controls.Add(logKind); Add(source, "Refresh logs", () => Task.CompletedTask);
        logPage.Controls.Add(logs); logPage.Controls.Add(source);
        Controls.Add(tabs); Controls.Add(header); Controls.Add(feedback);
        selector.SelectedIndexChanged += (_, _) => { if (!binding) SelectProfile(); };
        autoStart.Click += (_, _) => ChangeStartup(true);
        autoPanel.Click += (_, _) => ChangeStartup(false);
        mods.SelectedIndexChanged += (_, _) => EnableActions();
        timer.Tick += async (_, _) => await RefreshAsync();
        tabs.SelectedIndexChanged += async (_, _) => await RefreshAsync();
        logKind.SelectedIndexChanged += async (_, _) => await RefreshAsync();
        VisibleChanged += async (_, _) =>
        {
            if (Visible) await RefreshAsync();
        };
        timer.Start();
        var nearby = PanelProfiles.DefaultConfigPath;
        if (profiles.IsFirstRun && nearby != null) profiles.Add(Path.GetFileName(Path.GetDirectoryName(nearby)) ?? "Server", nearby);
        Bind(null);
    }

    private static ComboBox Choice() => new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private static FlowLayoutPanel Row() => new() { AutoSize = true, Dock = DockStyle.Top, WrapContents = true, Padding = new(2) };
    private TabPage Page(string title)
    {
        var page = new TabPage(title) { Padding = new(12), UseVisualStyleBackColor = true };
        tabs.TabPages.Add(page); return page;
    }
    private void Add(Control row, string label, Func<Task> action)
    {
        var button = new Button { Text = label, AutoSize = true, UseVisualStyleBackColor = true, Padding = new(6, 1, 6, 1) };
        actions[label] = button; row.Controls.Add(button);
        button.Click += async (_, _) =>
        {
            if (OperationPending) return;
            OperationPending = true; EnableActions();
            try { await action(); }
            catch (Exception e) { Report(e.Message); }
            finally { OperationPending = false; EnableActions(); await RefreshAsync(); }
        };
    }
    private void Report(string text)
    {
        if (Selected is { } profile) outcomes[profile.ConfigPath] = text;
        feedback.Text = text;
    }
    private void Bind(string? path)
    {
        binding = true;
        selector.Items.Clear();
        foreach (var p in profiles.Profiles) selector.Items.Add(p);
        selector.SelectedItem = profiles.Profiles.FirstOrDefault(p => string.Equals(p.ConfigPath,path,StringComparison.OrdinalIgnoreCase))
            ?? profiles.Profiles.FirstOrDefault();
        binding = false; SelectProfile();
    }
    private void SelectProfile()
    {
        backend = Selected is { } p ? new PanelBackend(p.ConfigPath) : null;
        snapshot = null; mods.Items.Clear(); roster.Items.Clear(); logs.Clear(); announcement.Clear();
        UpdateTitle();
        feedback.Text = Selected is { } s ? outcomes.GetValueOrDefault(s.ConfigPath, s.ConfigPath) : "Open an existing server config or create one.";
        status.Text = backend == null ? "No profile selected" : "Checking server…";
        autoStart.Checked = Selected?.AutoStart == true; autoPanel.Checked = profiles.StartPanelAtSignIn;
        EnableActions(); _ = RefreshAsync();
    }
    internal string TrayTitle { get; private set; } = "Nomad Drive server panel";

    private void UpdateTitle()
    {
        var name = snapshot?.ServerName ?? Selected?.Name;
        var suffix = snapshot switch
        {
            { PlayerCountKnown: true } live => $" ({live.Players}/{live.MaxPlayers})",
            { Running: false } stopped => $" (0/{stopped.MaxPlayers}, stopped)",
            { } pending => $" (?/{pending.MaxPlayers})",
            _ => ""
        };
        var trayName = name ?? "Nomad Drive server panel";
        // Keep the count visible even when the server name exceeds the tooltip budget.
        const int tooltipLimit = 63;
        var nameBudget = tooltipLimit - suffix.Length;
        if (trayName.Length > nameBudget)
        {
            var cut = nameBudget - 1;
            if (char.IsHighSurrogate(trayName[cut - 1])) cut--;
            trayName = trayName[..cut] + "…";
        }
        TrayTitle = trayName + suffix;
        Text = (name ?? "Nomad Drive | Server panel") + suffix;
    }

    private async Task RefreshAsync()
    {
        var current = backend;
        if (refreshing || OperationPending || current == null || IsDisposed) return;
        refreshing = true;
        try
        {
            var state = await current.StatusAsync();
            if (backend != current || IsDisposed) return;
            snapshot = state;
            UpdateTitle();
            if (!Visible) return;
            status.Text = $"{state.State.ToUpperInvariant()}    {state.Detail}" +
                (state.PlayerCountKnown ? $"    Players {state.Players}/{state.MaxPlayers}    Lobby {state.LobbyCode}"
                    : state.Running ? "    Player count unavailable" : "");
            Fill(roster, state.Names.Count > 0 ? state.Names : state.Running && !state.PlayerCountKnown ? ["Waiting for live player status"]
                : state.Players > 0 ? ["Player names unavailable from this host"] : []);
            if (tabs.SelectedIndex == 1)
            {
                var entries = await Task.Run(current.Mods);
                if (backend != current || IsDisposed) return;
                var selectedPath = (mods.SelectedItem as ModEntry)?.Mod.RelativePath;
                mods.BeginUpdate(); mods.Items.Clear();
                foreach (var mod in entries)
                {
                    var i = mods.Items.Add(new ModEntry(mod));
                    if (mod.RelativePath == selectedPath) mods.SelectedIndex = i;
                }
                mods.EndUpdate();
            }
            if (tabs.SelectedIndex == 3)
            {
                var kind = logKind.Text;
                var content = await Task.Run(() => current.TailLog(kind));
                if (backend == current && !IsDisposed && logKind.Text == kind && logs.Text != content)
                { logs.Text = content; logs.SelectionStart = logs.TextLength; logs.ScrollToCaret(); }
            }
        }
        catch (Exception e)
        {
            if (backend == current && !IsDisposed) { snapshot = null; UpdateTitle(); status.Text = e.Message; roster.Items.Clear(); }
        }
        finally
        {
            refreshing = false;
            if (!IsDisposed) { EnableActions(); if (backend != current) _ = RefreshAsync(); }
        }
    }
    private sealed record ModEntry(PanelMod Mod)
    {
        public override string ToString() => $"{(Mod.Enabled ? "Enabled" : "Disabled")}    {Mod.RelativePath}{(Mod.Protected ? "    (required)" : "")}";
    }
    private static void Fill(ListBox list, IReadOnlyList<string> items)
    {
        if (list.Items.Cast<string>().SequenceEqual(items)) return;
        list.BeginUpdate(); list.Items.Clear(); foreach (var item in items) list.Items.Add(item); list.EndUpdate();
    }
    private void EnableActions()
    {
        var stopped = snapshot?.Running == false;
        var ready = snapshot is { Running: true, State: "ready" or "attention" };
        var mod = (mods.SelectedItem as ModEntry)?.Mod;
        foreach (var (key, b) in actions)
            b.Enabled = !OperationPending && (key switch
            {
                "Open config" or "New config" => true,
                "Start" or "Install mod" or "Import saved world" => stopped,
                "Edit config" or "Edit server settings" => backend != null,
                "Enable" => stopped && mod is { Enabled: false, Protected: false },
                "Disable" => stopped && mod is { Enabled: true, Protected: false },
                "Forget profile" or "Refresh logs" => backend != null,
                _ => ready
            });
        selector.Enabled = !OperationPending;
        autoStart.Enabled = !OperationPending && backend != null;
        autoPanel.Enabled = !OperationPending;
    }
    private Task PickProfile(bool create)
    {
        using FileDialog dialog = create ? new SaveFileDialog { FileName = "server.json" } : new OpenFileDialog();
        dialog.Filter = "Server configuration (*.json)|*.json";
        if (dialog.ShowDialog(this) != DialogResult.OK) return Task.CompletedTask;
        var path = Path.GetFullPath(dialog.FileName);
        if (create)
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            System.Text.Json.JsonSerializer.Serialize(stream,new ServerConfig(),ServerConfig.Json);
        }
        var cfg = ServerConfig.Load(path);
        if (!profiles.Profiles.Any(p => string.Equals(p.ConfigPath,path,StringComparison.OrdinalIgnoreCase))) profiles.Add(cfg.Lobby.Name,path);
        Bind(path); return Task.CompletedTask;
    }
    private Task Forget()
    {
        if (Selected is { } p && Confirm($"Remove {p.Name} from this panel and its sign-in list? Its files and running server will be kept."))
        { profiles.Remove(p.ConfigPath); Bind(null); }
        return Task.CompletedTask;
    }
    private void ChangeStartup(bool server)
    {
        try
        {
            if (server && Selected != null) profiles.SetAutoStart(Selected.ConfigPath,autoStart.Checked);
            else if (!server) profiles.SetPanelStartup(autoPanel.Checked);
            Bind(Selected?.ConfigPath);
        }
        catch (Exception e) { autoStart.Checked = Selected?.AutoStart == true; autoPanel.Checked = profiles.StartPanelAtSignIn; Report(e.Message); }
    }
    private bool Confirm(string message) => MessageBox.Show(this,message,Text,MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)==DialogResult.Yes;
    private async Task Command(string label)
    {
        if (backend == null) return;
        if ((label == "Stop" || label == "Restart") && !Confirm($"{label} {Selected!.Name} after saving the world?")) return;
        if (label == "Announce" && string.IsNullOrWhiteSpace(announcement.Text)) { Report("Enter an announcement first."); return; }
        feedback.Text = label + " in progress…";
        var result = label == "Start" ? await backend.StartAsync() : await backend.CommandAsync(label == "Announce" ? "say" : label.ToLowerInvariant(), label == "Announce" ? announcement.Text : null);
        if (label == "Announce") announcement.Clear();
        Report(result);
    }
    private async Task Install()
    {
        using var pick = new OpenFileDialog { Filter = "BepInEx plugin (*.zip;*.dll)|*.zip;*.dll" };
        if (pick.ShowDialog(this) != DialogResult.OK || backend == null) return;
        await Task.Run(() => backend.InstallMod(pick.FileName)); Report("Mod installed.");
    }
    private async Task Toggle(bool enabled)
    {
        if (backend == null || mods.SelectedItem is not ModEntry m) return;
        await Task.Run(() => backend.SetModEnabled(m.Mod,enabled)); Report(enabled ? "Mod enabled." : "Mod disabled.");
    }
    private Task Edit()
    {
        if (Selected is not { } p) return Task.CompletedTask;
        using var editor = new SettingsWindow(p.ConfigPath);
        if (editor.ShowDialog(this) == DialogResult.OK)
            Report("Config saved. To apply startup settings, stop the server and start it again.");
        return Task.CompletedTask;
    }
    private async Task ImportWorld()
    {
        if (backend == null) return;
        using var picker = new FolderBrowserDialog { Description = "Select a saved world slot to copy into this profile's unused data directory" };
        if (picker.ShowDialog(this) == DialogResult.OK)
            Report(await backend.CommandAsync("import",picker.SelectedPath));
    }
    protected override void Dispose(bool disposing) { if (disposing) timer.Dispose(); base.Dispose(disposing); }
}
