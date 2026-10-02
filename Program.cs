using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StartupManager;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Contains("--self-test"))
        {
            try { ManagerForm.ValidateConfiguration(); Environment.Exit(0); }
            catch { Environment.Exit(1); }
            return;
        }
        Application.Run(new ManagerForm());
    }
}

sealed class ManagerForm : Form
{
    internal static readonly string Runtime = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "source", "repos", "StartupManager", ".runtime");
    static readonly string ConfigPath = Path.Combine(Runtime, "config.json");
    static readonly string PowerShell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
    readonly DataGridView grid = MakeGrid();
    readonly Label status = new() { AutoSize = true, ForeColor = Color.FromArgb(62, 70, 83), Margin = new Padding(0, 8, 0, 0) };
    readonly Label audio = new() { AutoSize = true, Text = "Checking audio apps…", ForeColor = Color.FromArgb(62, 70, 83) };
    readonly FlowLayoutPanel actions = new() { AutoSize = true, WrapContents = true, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 0) };
    JsonObject configuration = new();
    string loadedFile = "";
    bool dirty, loading, busy;

    public ManagerForm()
    {
        Text = "Startup Manager";
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(247, 248, 251);
        MinimumSize = new Size(900, 660);
        Size = new Size(1040, 760);
        StartPosition = FormStartPosition.CenterScreen;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 7 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 7; i++) layout.RowStyles.Add(new RowStyle(i == 4 ? SizeType.Percent : SizeType.AutoSize, i == 4 ? 100 : 0));
        var heading = new Label { Text = "Audio first. Everything else follows.", Font = new Font("Segoe UI", 20, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
        layout.Controls.Add(heading, 0, 0);
        var priority = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 16) };
        priority.Controls.Add(PriorityCard("1  Stream Deck", "Wait for a responsive process + initialization time."));
        priority.Controls.Add(PriorityCard("2  Wave Link 3", "Wait for its service and fresh local server."));
        layout.Controls.Add(priority, 0, 1);
        layout.Controls.Add(audio, 0, 2);
        layout.Controls.Add(new Label { Text = "Then launch these apps — checked apps will start at sign-in.", AutoSize = true, Margin = new Padding(0, 18, 0, 8) }, 0, 3);
        grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Enabled", HeaderText = "Start", Width = 65 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "App", Width = 235, ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Launch", HeaderText = "Launch details", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true });
        grid.CurrentCellDirtyStateChanged += (_, _) => { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        grid.CellValueChanged += (_, e) => { if (!loading && e.RowIndex >= 0) SetDirty(); };
        layout.Controls.Add(grid, 0, 4);
        AddButton("Save changes", (_, _) => Guard(Save));
        AddButton("Add app…", (_, _) => Guard(AddApp));
        AddButton("Edit…", (_, _) => Guard(EditSelected));
        AddButton("Remove", (_, _) => Guard(RemoveSelected));
        AddButton("Move up", (_, _) => MoveSelected(-1));
        AddButton("Move down", (_, _) => MoveSelected(1));
        AddButton("Import startup apps…", async (_, _) => await GuardAsync(ImportApps));
        AddButton("Check readiness", async (_, _) => await GuardAsync(RefreshReadiness));
        AddButton("Install / repair sequence…", async (_, _) => await GuardAsync(InstallSequence));
        AddButton("View log", (_, _) => Guard(() => Process.Start(new ProcessStartInfo("notepad.exe", Path.Combine(Runtime, "startup.log")) { UseShellExecute = true })));
        AddButton("Restore original startup…", async (_, _) => await GuardAsync(Restore));
        layout.Controls.Add(actions, 0, 5);
        layout.Controls.Add(status, 0, 6);
        Controls.Add(layout);
        Shown += async (_, _) => await GuardAsync(async () => { LoadConfig(); await RefreshReadiness(); });
        FormClosing += (_, e) => { if (busy) { e.Cancel = true; return; } if (dirty && MessageBox.Show(this, "Close without saving your changes?", "Unsaved changes", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) e.Cancel = true; };
    }
    static Control PriorityCard(string title, string description)
    {
        var panel = new TableLayoutPanel { AutoSize = true, BackColor = Color.White, Padding = new Padding(14), Margin = new Padding(0, 0, 14, 0), MinimumSize = new Size(360, 80), ColumnCount = 1 };
        panel.Controls.Add(new Label { Text = title, AutoSize = true, Font = new Font("Segoe UI", 13, FontStyle.Bold), ForeColor = Color.FromArgb(25, 72, 133) });
        panel.Controls.Add(new Label { Text = description, AutoSize = true, Margin = new Padding(0, 6, 0, 0) });
        return panel;
    }
    static DataGridView MakeGrid() => new() { Dock = DockStyle.Fill, BackgroundColor = Color.White, BorderStyle = BorderStyle.None, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, RowHeadersVisible = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells, ColumnHeadersHeight = 36, EnableHeadersVisualStyles = false, ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(228, 234, 242), ForeColor = Color.FromArgb(32, 44, 62), Font = new Font("Segoe UI", 10, FontStyle.Bold) }, DefaultCellStyle = new DataGridViewCellStyle { Padding = new Padding(4), SelectionBackColor = Color.FromArgb(219, 232, 251), SelectionForeColor = Color.Black } };
    void AddButton(string label, EventHandler action)
    {
        var button = new Button { Text = label, AutoSize = true, Height = 34, Padding = new Padding(7, 3, 7, 3), Margin = new Padding(0, 0, 8, 8), FlatStyle = FlatStyle.System };
        button.Click += action;
        actions.Controls.Add(button);
    }
    static string S(JsonObject obj, string key) => obj[key]?.GetValue<string>() ?? "";
    static bool IsEnabled(JsonObject obj) => obj["Enabled"]?.GetValue<bool>() ?? true;
    static string DisplayName(JsonObject obj)
    {
        string name = S(obj, "Name");
        if (name.StartsWith("GoogleChromeAutoLaunch_")) return "Chrome background startup";
        if (name.StartsWith("28017CharlesMilette.TranslucentTB")) return "TranslucentTB";
        if (name == "LGHUB") return "Logitech G HUB";
        if (name == "RazerAppEngine") return "Razer Synapse";
        if (name == "\\PowerToys\\Autorun for Simms") return "PowerToys";
        return name.TrimStart('\\');
    }
    static string Details(JsonObject obj) => S(obj, "Kind") switch { "Command" => S(obj, "FileName") + " " + S(obj, "Arguments"), "PackageApp" => S(obj, "AppId"), "Task" => S(obj, "TaskPath") + S(obj, "TaskName"), _ => "Unknown launch type" };
    internal static void ValidateConfiguration()
    {
        var config = JsonNode.Parse(File.ReadAllText(ConfigPath))!.AsObject();
        if (!File.Exists(S(config, "StreamDeckPath"))) throw new InvalidDataException("Stream Deck executable is missing.");
        if (config["Apps"] is not JsonArray entries) throw new InvalidDataException("Startup app list is missing.");
        foreach (var node in entries)
        {
            var entry = node!.AsObject();
            if (string.IsNullOrWhiteSpace(S(entry, "Name"))) throw new InvalidDataException("An app has no name.");
            if (S(entry, "Kind") is not ("Command" or "PackageApp" or "Task")) throw new InvalidDataException("Unknown launch type.");
            if (S(entry, "Kind") == "Command" && !Path.IsPathFullyQualified(S(entry, "FileName"))) throw new InvalidDataException("Executable path must be absolute.");
        }
        foreach (var file in new[] { "Start-OrderedApps.ps1", "StartupManager.Common.ps1", "Export-StartupState.ps1", "Import-StartupApps.ps1", "Restore-StartupOrder.ps1", "backup.json" })
            if (!File.Exists(Path.Combine(Runtime, file))) throw new FileNotFoundException(file);
    }
    void LoadConfig()
    {
        ValidateConfiguration();
        loadedFile = File.ReadAllText(ConfigPath);
        configuration = JsonNode.Parse(loadedFile)!.AsObject();
        loading = true;
        grid.Rows.Clear();
        foreach (var node in configuration["Apps"]!.AsArray()) AddRow(node!.AsObject().DeepClone().AsObject());
        loading = false; dirty = false;
        status.Text = "Saved configuration · Windows runs this sequence without the manager being open.";
    }
    void AddRow(JsonObject entry)
    {
        int index = grid.Rows.Add(IsEnabled(entry), DisplayName(entry), Details(entry));
        grid.Rows[index].Tag = entry;
    }
    void SetDirty() { dirty = true; status.Text = "Unsaved changes — click Save changes to apply them at the next sign-in."; }
    void Save()
    {
        grid.EndEdit();
        if (File.ReadAllText(ConfigPath) != loadedFile) throw new IOException("The configuration changed outside this window. Close and reopen the manager before saving.");
        var entries = new JsonArray();
        foreach (DataGridViewRow row in grid.Rows)
        {
            var obj = ((JsonObject)row.Tag!).DeepClone().AsObject();
            obj["Enabled"] = Convert.ToBoolean(row.Cells[0].Value);
            entries.Add(obj);
        }
        var next = configuration.DeepClone().AsObject(); next["Apps"] = entries;
        string text = next.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        string temporary = ConfigPath + ".gui.tmp";
        File.WriteAllText(temporary, text);
        File.Move(temporary, ConfigPath, true);
        loadedFile = text; configuration = next; dirty = false;
        status.Text = "Saved. Changes take effect at the next sign-in.";
    }
    bool SaveBeforeAction() { if (!dirty) return true; if (MessageBox.Show(this, "Save your list changes before continuing?", "Save changes", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return false; Save(); return true; }
    void AddApp()
    {
        using var picker = new OpenFileDialog { Title = "Choose an app to start after Wave Link", Filter = "Apps and shortcuts|*.exe;*.lnk|All files|*.*", CheckFileExists = true };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        var obj = new JsonObject { ["Name"] = Path.GetFileNameWithoutExtension(picker.FileName), ["Kind"] = "Command", ["FileName"] = picker.FileName, ["Arguments"] = "", ["WorkingDirectory"] = Path.GetDirectoryName(picker.FileName), ["Enabled"] = true };
        if (EditDialog(obj)) { AddRow(obj); SetDirty(); }
    }
    void EditSelected()
    {
        if (grid.SelectedRows.Count == 0) return;
        var row = grid.SelectedRows[0]; var obj = ((JsonObject)row.Tag!).DeepClone().AsObject();
        if (S(obj, "Kind") != "Command") { MessageBox.Show(this, "Packaged apps and scheduled tasks retain their registered launch settings. You can change whether they start or move them in this list."); return; }
        if (EditDialog(obj)) { row.Tag = obj; row.Cells[1].Value = S(obj, "Name"); row.Cells[2].Value = Details(obj); SetDirty(); }
    }
    bool EditDialog(JsonObject obj)
    {
        using var dialog = new Form { Text = "App launch settings", Width = 720, Height = 320, StartPosition = FormStartPosition.CenterParent, Font = Font, MinimizeBox = false, MaximizeBox = false, FormBorderStyle = FormBorderStyle.FixedDialog };
        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 2, RowCount = 4 };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        string[] keys = ["Name", "FileName", "Arguments", "WorkingDirectory"];
        string[] labels = ["Name", "App path", "Arguments", "Start in"];
        var inputs = new List<TextBox>();
        for (int i = 0; i < keys.Length; i++) { fields.Controls.Add(new Label { Text = labels[i], AutoSize = true, Margin = new Padding(0, 8, 0, 0) }, 0, i); var input = new TextBox { Text = S(obj, keys[i]), Dock = DockStyle.Fill }; inputs.Add(input); fields.Controls.Add(input, 1, i); }
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        var ok = new Button { Text = "Done", DialogResult = DialogResult.None, AutoSize = true }; var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        ok.Click += (_, _) => { if (string.IsNullOrWhiteSpace(inputs[0].Text) || !Path.IsPathFullyQualified(inputs[1].Text) || !File.Exists(inputs[1].Text)) { MessageBox.Show(dialog, "Enter a name and the absolute path of an existing app or shortcut."); return; } dialog.DialogResult = DialogResult.OK; };
        footer.Controls.Add(ok); footer.Controls.Add(cancel); dialog.Controls.Add(fields); dialog.Controls.Add(footer); dialog.AcceptButton = ok; dialog.CancelButton = cancel;
        if (dialog.ShowDialog(this) != DialogResult.OK) return false;
        for (int i = 0; i < keys.Length; i++) obj[keys[i]] = inputs[i].Text;
        return true;
    }
    void RemoveSelected()
    {
        if (grid.SelectedRows.Count == 0) return;
        if (MessageBox.Show(this, "Remove this app from ordered startup? Its original startup entry will remain disabled. Use Restore original startup to undo the entire setup.", "Remove from startup list", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        grid.Rows.Remove(grid.SelectedRows[0]); SetDirty();
    }
    void MoveSelected(int delta)
    {
        if (grid.SelectedRows.Count == 0) return;
        var row = grid.SelectedRows[0]; int target = row.Index + delta;
        if (target < 0 || target >= grid.Rows.Count) return;
        grid.Rows.Remove(row); grid.Rows.Insert(target, row); row.Selected = true; grid.CurrentCell = row.Cells[1]; SetDirty();
    }
    async Task RefreshReadiness()
    {
        string text = await RunScript("Export-StartupState.ps1");
        var state = JsonNode.Parse(text)!.AsObject();
        audio.Text = $"Stream Deck: {(state["StreamDeckRunning"]!.GetValue<bool>() ? "running" : "not running")}     Wave Link: {(state["WaveLinkReady"]!.GetValue<bool>() ? "ready" : "not ready")}     Service: {S(state, "Service")}     Startup task: {S(state, "Task")}";
        if (!dirty) status.Text = S(state, "Task") == "Not installed" ? "The sign-in sequence is not installed. Click Install / repair sequence to enable it." : "Saved configuration · Windows runs this sequence without the manager being open.";
    }
    async Task InstallSequence()
    {
        if (!SaveBeforeAction()) return;
        if (MessageBox.Show(this, "Install or repair the sign-in sequence using the saved configuration? Windows will ask for administrator access.", "Install startup sequence", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        await RunElevated("Install-StartupOrder.ps1"); await RefreshReadiness();
        status.Text = "Startup sequence installed. It will run at your next sign-in.";
    }
    async Task ImportApps()
    {
        if (!SaveBeforeAction()) return;
        var candidates = JsonNode.Parse(await RunScript("Export-StartupState.ps1", "-Candidates"))!.AsArray();
        if (candidates.Count == 0) { MessageBox.Show(this, "No additional enabled desktop startup apps were found. Previously disabled apps are left alone.", "Startup entries"); return; }
        using var dialog = new Form { Text = "Import enabled startup apps", Width = 840, Height = 500, StartPosition = FormStartPosition.CenterParent, Font = Font };
        var list = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, HorizontalScrollbar = true };
        foreach (var node in candidates) { var candidate = node!.AsObject(); list.Items.Add(S(candidate, "Name") + "  —  " + S(candidate, "Origin")); }
        var note = new Label { Text = "Selected entries will stop launching independently and will launch after Wave Link.\nWindows will ask for administrator access. System services and security startup are excluded.", Dock = DockStyle.Top, Height = 65, Padding = new Padding(10) };
        var button = new Button { Text = "Import selected", Dock = DockStyle.Bottom, Height = 42 };
        button.Click += (_, _) => { if (list.CheckedIndices.Count > 0) dialog.DialogResult = DialogResult.OK; };
        dialog.Controls.Add(list); dialog.Controls.Add(note); dialog.Controls.Add(button);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var ids = new JsonArray(); foreach (int i in list.CheckedIndices) ids.Add(S(candidates[i]!.AsObject(), "Id"));
        File.WriteAllText(Path.Combine(Runtime, "pending-import.json"), ids.ToJsonString());
        string resultPath = Path.Combine(Runtime, "import-result.json"); if (File.Exists(resultPath)) File.Delete(resultPath);
        await RunElevated("Import-StartupApps.ps1");
        if (!File.Exists(resultPath)) throw new IOException("The import did not complete. The current configuration was left in place.");
        var result = JsonNode.Parse(File.ReadAllText(resultPath))!.AsObject();
        if (!result["Success"]!.GetValue<bool>()) throw new IOException(S(result, "Error"));
        LoadConfig(); await RefreshReadiness(); status.Text = $"Imported {result["Count"]} startup app(s). They will follow Wave Link at the next sign-in.";
    }
    async Task Restore()
    {
        if (MessageBox.Show(this, "Restore the captured original startup settings and remove the ordered startup task? This will also restore Wave Link's original service recovery settings. Windows will ask for administrator access.", "Restore original startup", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        await RunElevated("Restore-StartupOrder.ps1"); dirty = false;
        MessageBox.Show(this, "Original startup settings restored. The custom startup task has been removed. Wave Link's service was left running.", "Restored");
        busy = false; Close();
    }
    static ProcessStartInfo ScriptInfo(string file, bool elevated = false)
    {
        var info = new ProcessStartInfo(PowerShell) { UseShellExecute = elevated, CreateNoWindow = !elevated, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(Runtime, file) }) info.ArgumentList.Add(arg);
        if (elevated) info.Verb = "runas";
        else { info.RedirectStandardOutput = true; info.RedirectStandardError = true; }
        return info;
    }
    static async Task<string> RunScript(string file, params string[] args)
    {
        var info = ScriptInfo(file); foreach (string arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("Could not start the startup helper.");
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        try { await process.WaitForExitAsync(timeout.Token); } catch (OperationCanceledException) { process.Kill(true); throw new TimeoutException("The startup check timed out. Your settings were not changed."); }
        string text = await output; string error = await errors;
        if (process.ExitCode != 0) throw new IOException(string.IsNullOrWhiteSpace(error) ? "The startup helper failed." : error);
        return text;
    }
    static async Task RunElevated(string file)
    {
        using var process = Process.Start(ScriptInfo(file, true)) ?? throw new IOException("Administrator access was not granted.");
        await process.WaitForExitAsync(); if (process.ExitCode != 0) throw new IOException("The change did not complete. See the helper result or log for details.");
    }
    void Guard(Action action) { try { action(); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Startup Manager", MessageBoxButtons.OK, MessageBoxIcon.Error); } }
    async Task GuardAsync(Func<Task> action)
    {
        if (busy) return; busy = true; actions.Enabled = false; grid.Enabled = false;
        try { await action(); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Startup Manager", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { busy = false; actions.Enabled = true; grid.Enabled = true; }
    }
}
