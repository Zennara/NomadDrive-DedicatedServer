using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NomadDrive.Server;

namespace NomadDrive.Panel;

internal sealed class SettingsWindow : Form
{
    private readonly string path;
    private readonly byte[]? original;
    private readonly ServerConfig config;
    private readonly List<Action> apply = [];
    private readonly TabControl pages = new() { Dock = DockStyle.Fill };
    private readonly Button save = new() { Text = "Save settings", AutoSize = true };

    internal SettingsWindow(string path, ServerConfig? initial = null)
    {
        this.path = path;
        if (initial == null) original = File.ReadAllBytes(path);
        config = initial ?? ReadConfig(original!);
        Text = "Server settings"; Size = new(760, 550); MinimumSize = new(620, 430);
        if (initial != null) { Text = "Configure new server"; save.Text = "Continue"; }
        Icon = Program.PanelIcon;
        StartPosition = FormStartPosition.CenterParent;
        Section("Server",config);
        Section("Lobby",config.Lobby); Section("World",config.World);
        Section("Backups",config.Backups); Section("Restarts",config.Restarts); Section("Recovery",config.Recovery);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new(8) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        footer.Controls.Add(cancel); footer.Controls.Add(save);
        var notice = new Label { Text = initial == null
                ? "Changes apply on the next full stop and start. Stop the server before changing directories."
                : "Choose the server settings. The new server will stay stopped.",
            Dock = DockStyle.Top, AutoSize = true, Padding = new(10) };
        Controls.Add(pages); Controls.Add(footer); Controls.Add(notice); CancelButton = cancel;
        save.Click += (_, _) =>
        {
            save.Enabled = false;
            try
            {
                foreach (var update in apply) update();
                if (original != null) SaveConfig(path,original,config);
                else config.Validate();
                DialogResult = DialogResult.OK;
            }
            catch (Exception e) { MessageBox.Show(this,e.Message,"Settings",MessageBoxButtons.OK,MessageBoxIcon.Warning); }
            finally { if (!IsDisposed) save.Enabled = true; }
        };
    }

    internal ServerConfig Config => config;

    private static ServerConfig ReadConfig(byte[] contents)
    {
        using var input = new MemoryStream(contents);
        using var reader = new StreamReader(input, Encoding.UTF8, true);
        return JsonSerializer.Deserialize<ServerConfig>(reader.ReadToEnd(), ServerConfig.Json)
            ?? throw new InvalidDataException("Empty configuration.");
    }

    internal static void SaveConfig(string path, byte[] original, ServerConfig config)
    {
        SafeFiles.RejectReparseAncestors(path);
        var temp = Path.Combine(Path.GetDirectoryName(path)!, ".panel-" + Guid.NewGuid().ToString("N") + ".json");
        var locks = new Stack<FileStream>();
        try
        {
            using (var file = new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                JsonSerializer.Serialize(file,config,ServerConfig.Json);
            var validated = ServerConfig.Load(temp);
            var prior = ReadConfig(original);
            var parent = Path.GetDirectoryName(Path.GetFullPath(path))!;
            string ResolvePrior(string value) => string.IsNullOrWhiteSpace(value) ? "" :
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(value, parent));
            var previousData = ResolvePrior(prior.DataDirectory);
            var previousGame = ResolvePrior(prior.GameDirectory);
            var changingDirectories = !StringComparer.OrdinalIgnoreCase.Equals(previousData, Path.TrimEndingDirectorySeparator(validated.DataDirectory)) ||
                !StringComparer.OrdinalIgnoreCase.Equals(previousGame, Path.TrimEndingDirectorySeparator(validated.GameDirectory));
            // A running supervisor has its startup settings in memory, but changing
            // its paths could detach the panel from the process it still manages.
            if (changingDirectories)
            {
                foreach (var root in new[] { previousData, previousGame, validated.DataDirectory, validated.GameDirectory }
                    .Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
                {
                    SafeFiles.RejectReparseAncestors(Path.Combine(root,"server.lock"));
                    locks.Push(SafeFiles.AcquireLock(root));
                    RuntimeGuard.RejectLiveGame(root);
                }
            }
            if (!original.SequenceEqual(File.ReadAllBytes(path)))
                throw new IOException("The config changed on disk. Reopen settings before saving.");
            File.Replace(temp,path,null);
        }
        finally
        {
            while (locks.TryPop(out var handle)) handle.Dispose();
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private void Section(string title, object value)
    {
        var page = new TabPage(title) { AutoScroll = true, UseVisualStyleBackColor = true };
        var grid = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2, Padding = new(12) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        foreach (var p in value.GetType().GetProperties())
        {
            if (p.Name == nameof(ServerConfig.SchemaVersion)) continue;
            var type = p.PropertyType;
            if (type != typeof(string) && type != typeof(int) && type != typeof(bool) && type != typeof(int[])) continue;
            var name = Regex.Replace(p.Name,"(?<=[a-z])(?=[A-Z])"," ");
            var label = new Label { AutoSize = true, Text = name, Anchor = AnchorStyles.Left, Margin = new(3,8,14,8) };
            Control field;
            if (type == typeof(bool))
            {
                var check = new CheckBox { Checked = (bool)p.GetValue(value)! };
                apply.Add(() => p.SetValue(value,check.Checked)); field = check;
            }
            else if (p.Name == nameof(WorldConfig.Preset))
            {
                var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
                box.Items.AddRange(["Cozy","Normal","Nomad","Custom"]);
                box.SelectedItem = p.GetValue(value); apply.Add(() => p.SetValue(value,box.SelectedItem)); field = box;
            }
            else
            {
                var box = new TextBox { Text = type == typeof(int[]) ? string.Join(", ",(int[])p.GetValue(value)!) : p.GetValue(value)?.ToString(),
                    UseSystemPasswordChar = p.Name == nameof(LobbyConfig.Password) };
                apply.Add(() =>
                {
                    try
                    {
                        var input = box.Text ?? "";
                        object parsed = type == typeof(int) ? int.Parse(input) : type == typeof(int[])
                            ? input.Split(',',StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray() : input;
                        p.SetValue(value,parsed);
                    }
                    catch (Exception e) when (e is FormatException or OverflowException)
                    { throw new InvalidDataException(title + " / " + name + " requires whole numbers."); }
                });
                field = box;
            }
            field.AccessibleName = name; field.Dock = DockStyle.Fill;
            grid.Controls.Add(label,0,grid.RowCount); grid.Controls.Add(field,1,grid.RowCount++);
        }
        page.Controls.Add(grid); pages.TabPages.Add(page);
    }
}
