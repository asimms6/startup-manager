using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using StartupManager.Core;

namespace StartupManager;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (BackendCommands.IsCommand(args)) { Environment.ExitCode = BackendCommands.Execute(args); return; }
        ApplicationConfiguration.Initialize();
        if (args.Contains("--self-test"))
        {
            try { ManagerForm.ValidateConfiguration(); Environment.Exit(0); }
            catch { Environment.Exit(1); }
            return;
        }
        // Task Scheduler starts a desktop process without an inherited packaged registry view.
        if (!args.Contains("--desktop"))
        {
            try
            {
                BackendCommands.LaunchDesktop();
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Startup Manager"); }
            return;
        }
        try { BackendCommands.CleanupDesktopTask(); } catch (Exception ex) { Debug.WriteLine(ex.Message); }
        int stateIndex = Array.IndexOf(args, "--state-directory");
        string? stateDirectory = stateIndex >= 0 && stateIndex + 1 < args.Length ? Path.GetFullPath(args[stateIndex + 1]) : null;
        Application.Run(new ManagerForm(stateDirectory));
    }
}

sealed partial class ManagerForm : Form
{
    internal readonly string Runtime;
    string ConfigPath => Path.Combine(Runtime, "config.json");
    readonly DataGridView grid = MakeGrid();
    readonly Label status = new() { AutoSize = true, ForeColor = MutedColor, Margin = new Padding(0, 8, 0, 0) };
    readonly Label audio = new() { AutoSize = true, Text = "Checking startup task…", ForeColor = MutedColor };
    readonly Label conflicts = new() { AutoSize = true, ForeColor = WarningColor };
    readonly FlowLayoutPanel actions = new() { AutoSize = true, WrapContents = true, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 0) };
    JsonObject configuration = new();
    string loadedFile = "";
    bool dirty, loading, busy, refreshing;
    readonly ListBox groupList = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    readonly FlowLayoutPanel groupActions = new() { Dock = DockStyle.Bottom, Height = 150, WrapContents = true };
    JsonObject? selectedGroup;
    JsonArray Groups => configuration["Groups"]!.AsArray();
    sealed record GroupChoice(JsonObject Group, int Position) { public override string ToString() => $"{Position}. {S(Group, "Name")}{(IsEnabled(Group) ? "" : " (disabled)")}"; }

    public ManagerForm(string? stateDirectory = null)
    {
        Runtime = stateDirectory ?? AppPaths.StateDirectory;
        BuildBoardShell();
        Shown += async (_, _) => await GuardAsync(async () => { await RunBackend("--initialize"); LoadConfig(); await RefreshReadiness(); });
        var timer = new System.Windows.Forms.Timer { Interval = 30000 };
        timer.Tick += async (_, _) =>
        {
            if (busy || refreshing || !File.Exists(ConfigPath)) return;
            refreshing = true;
            try { await RefreshReadiness(); }
            catch (Exception ex) { conflicts.ForeColor = WarningColor; conflicts.Text = "Startup check failed: " + ex.Message; }
            finally { refreshing = false; }
        };
        timer.Start(); FormClosed += (_, _) => timer.Dispose();
        FormClosing += (_, e) => { if (busy) { e.Cancel = true; return; } if (dirty && MessageBox.Show(this, "Close without saving your changes?", "Unsaved changes", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) e.Cancel = true; };
    }

    void CommitGroupRows()
    {
        if (selectedGroup == null || loading) return;
        grid.EndEdit();
        var apps = new JsonArray();
        foreach (DataGridViewRow row in grid.Rows)
        {
            var obj = ((JsonObject)row.Tag!).DeepClone().AsObject();
            obj["Enabled"] = Convert.ToBoolean(row.Cells[0].Value);
            apps.Add(obj);
        }
        selectedGroup["Apps"] = apps;
    }
    void RebuildGroups(string? selectedId = null)
    {
        loading = true;
        groupList.Items.Clear();
        int index = 0, selection = 0;
        foreach (var node in Groups)
        {
            var group = node!.AsObject();
            if (S(group, "Id") == selectedId) selection = index;
            groupList.Items.Add(new GroupChoice(group, ++index));
        }
        if (groupList.Items.Count > 0) groupList.SelectedIndex = selection;
        loading = false; ShowSelectedGroup();
    }
    void ShowSelectedGroup()
    {
        selectedGroup = (groupList.SelectedItem as GroupChoice)?.Group;
        loading = true; grid.Rows.Clear();
        if (selectedGroup != null)
            foreach (var node in selectedGroup["Apps"]!.AsArray()) AddRow(node!.AsObject().DeepClone().AsObject());
        loading = false; RenderBoard();
    }
    void AddGroup()
    {
        CommitGroupRows();
        var group = new JsonObject { ["Id"] = Guid.NewGuid().ToString("N"), ["Name"] = $"Group {Groups.Count + 1}", ["Enabled"] = true, ["OnFailure"] = "Stop", ["DelayAfterSeconds"] = 0, ["Apps"] = new JsonArray() };
        if (!GroupDialog(group)) return;
        Groups.Add(group); RebuildGroups(S(group, "Id")); SetDirty();
    }
    void EditGroup()
    {
        if (selectedGroup == null) return;
        CommitGroupRows();
        if (GroupDialog(selectedGroup)) { RebuildGroups(S(selectedGroup, "Id")); SetDirty(); }
    }
    bool GroupDialog(JsonObject group)
    {
        using var dialog = new Form { Text = "Startup group", Width = 540, Height = 300, StartPosition = FormStartPosition.CenterParent, Font = Font, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 2 };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var name = new TextBox { Text = S(group, "Name"), Dock = DockStyle.Fill };
        var enabled = new CheckBox { Checked = IsEnabled(group), Text = "Launch this group", AutoSize = true };
        var delay = new NumericUpDown { Minimum = 0, Maximum = 3600, Value = group["DelayAfterSeconds"]?.GetValue<decimal>() ?? 0 };
        var failure = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        failure.Items.AddRange(["Stop remaining groups", "Continue to next group"]); failure.SelectedIndex = S(group, "OnFailure") == "Continue" ? 1 : 0;
        string[] labels = ["Name", "Enabled", "Delay after (seconds)", "If an app fails"];
        Control[] controls = [name, enabled, delay, failure];
        for (int i = 0; i < labels.Length; i++) { fields.Controls.Add(new Label { Text = labels[i], AutoSize = true }, 0, i); fields.Controls.Add(controls[i], 1, i); }
        var done = new RoundedButton { Text = "Done", Dock = DockStyle.Bottom, Height = 40 };
        done.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(name.Text)) dialog.DialogResult = DialogResult.OK; };
        dialog.Controls.Add(fields); dialog.Controls.Add(done); dialog.AcceptButton = done;
        ThemeDialog(dialog);
        if (dialog.ShowDialog(this) != DialogResult.OK) return false;
        group["Name"] = name.Text.Trim(); group["Enabled"] = enabled.Checked; group["DelayAfterSeconds"] = delay.Value; group["OnFailure"] = failure.SelectedIndex == 1 ? "Continue" : "Stop";
        return true;
    }
    void MoveGroup(int delta)
    {
        if (selectedGroup == null) return;
        CommitGroupRows();
        int index = groupList.SelectedIndex, target = index + delta;
        if (target < 0 || target >= Groups.Count) return;
        var group = selectedGroup;
        Groups.RemoveAt(index); Groups.Insert(target, group); RebuildGroups(S(group, "Id")); SetDirty();
    }
    void DeleteGroup()
    {
        if (selectedGroup == null) return;
        if (MessageBox.Show(this, "Delete this group and remove its apps from the ordered sequence? Their original startup entries remain disabled until you restore the original settings.", "Delete group", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        Groups.RemoveAt(groupList.SelectedIndex); selectedGroup = null; RebuildGroups(); SetDirty();
    }
    void MoveAppToGroup()
    {
        if (selectedGroup == null || grid.SelectedRows.Count == 0 || Groups.Count < 2) return;
        int appIndex = grid.SelectedRows[0].Index; CommitGroupRows();
        using var dialog = new Form { Text = "Move app to group", Width = 450, Height = 180, StartPosition = FormStartPosition.CenterParent, Font = Font };
        var targets = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
        int position = 0;
        foreach (var node in Groups) { var group = node!.AsObject(); position++; if (group != selectedGroup) targets.Items.Add(new GroupChoice(group, position)); }
        targets.SelectedIndex = 0;
        var move = new RoundedButton { Text = "Move", Dock = DockStyle.Bottom, Height = 40, DialogResult = DialogResult.OK };
        dialog.Controls.Add(targets); dialog.Controls.Add(move);
        ThemeDialog(dialog);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var source = selectedGroup["Apps"]!.AsArray();
        var app = source[appIndex]!; source.RemoveAt(appIndex);
        ((GroupChoice)targets.SelectedItem!).Group["Apps"]!.AsArray().Add(app);
        ShowSelectedGroup(); SetDirty();
    }

    static DataGridView MakeGrid() => new() { AllowUserToAddRows = false, AllowUserToDeleteRows = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
    static string S(JsonObject obj, string key) => obj[key]?.GetValue<string>() ?? "";
    static bool IsEnabled(JsonObject obj) => obj["Enabled"]?.GetValue<bool>() ?? true;
    static string DisplayName(JsonObject obj)
    {
        string name = S(obj, "Name");
        if (name.StartsWith("GoogleChromeAutoLaunch_")) return "Chrome background startup";
        if (name.StartsWith("28017CharlesMilette.TranslucentTB")) return "TranslucentTB";
        if (name == "LGHUB") return "Logitech G HUB";
        if (name == "RazerAppEngine") return "Razer Synapse";
        if (name.StartsWith("\\PowerToys\\Autorun for ")) return "PowerToys";
        return name.TrimStart('\\');
    }
    static string Details(JsonObject obj) => S(obj, "Kind") switch { "Command" => S(obj, "FileName") + " " + S(obj, "Arguments"), "PackageApp" => S(obj, "AppId"), "Task" => S(obj, "TaskPath") + S(obj, "TaskName"), _ => "Unknown launch type" };
    internal static void ValidateConfiguration(string? stateDirectory = null)
    {
        string directory = stateDirectory ?? AppPaths.StateDirectory;
        var config = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "config.json")))!.AsObject();
        if (S(config, "UserSid") != System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value) throw new InvalidDataException("This folder contains another account's configuration. Use a fresh release ZIP for this account.");
        if (config["SchemaVersion"]?.GetValue<int>() != 2 || config["Groups"] is not JsonArray groups) throw new InvalidDataException("Startup group configuration is missing.");
        foreach (var group in groups)
        foreach (var node in group!["Apps"]!.AsArray())
        {
            var entry = node!.AsObject();
            if (string.IsNullOrWhiteSpace(S(entry, "Name"))) throw new InvalidDataException("An app has no name.");
            if (S(entry, "Kind") is not ("Command" or "PackageApp" or "Task")) throw new InvalidDataException("Unknown launch type.");
            if (S(entry, "Kind") == "Command" && !Path.IsPathFullyQualified(S(entry, "FileName"))) throw new InvalidDataException("Executable path must be absolute.");
        }
        Configuration.Validate(config);
        foreach (var file in new[] { "backup.json" })
            if (!File.Exists(Path.Combine(directory, file))) throw new FileNotFoundException(file);
    }
    void LoadConfig()
    {
        string? previousGroupId = selectedGroup == null ? null : S(selectedGroup, "Id");
        ValidateConfiguration(Runtime);
        loadedFile = File.ReadAllText(ConfigPath);
        configuration = JsonNode.Parse(loadedFile)!.AsObject();
        selectedGroup = null; RebuildGroups(previousGroupId); dirty = false;
        status.Text = "Saved configuration · Windows runs this sequence without the manager being open.";
    }
    void AddRow(JsonObject entry)
    {
        int index = grid.Rows.Add(IsEnabled(entry), DisplayName(entry), Details(entry), S(entry, "WaitMode") is "" or "Launch" ? "Launch + delay" : S(entry, "WaitMode"));
        grid.Rows[index].Tag = entry;
    }
    void SetDirty() { dirty = true; status.Text = "Unsaved changes · Save to apply at your next sign-in."; QueueBoardRefresh(); }
    void Save()
    {
        grid.EndEdit();
        if (File.ReadAllText(ConfigPath) != loadedFile) throw new IOException("The configuration changed outside this window. Close and reopen the manager before saving.");
        CommitGroupRows();
        var next = configuration.DeepClone().AsObject();
        string text = next.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        string temporary = ConfigPath + ".gui.tmp";
        File.WriteAllText(temporary, text);
        File.Move(temporary, ConfigPath, true);
        string? selectedId = selectedGroup == null ? null : S(selectedGroup, "Id");
        loadedFile = text; configuration = next; RebuildGroups(selectedId); dirty = false;
        status.Text = "Saved. Changes take effect at the next sign-in.";
    }
    bool SaveBeforeAction() { if (!dirty) return true; if (MessageBox.Show(this, "Save your list changes before continuing?", "Save changes", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return false; Save(); return true; }
    void AddApp()
    {
        if (selectedGroup == null) { MessageBox.Show(this, "Create or select a group first."); return; }
        using var picker = new OpenFileDialog { Title = "Choose an app for this group", Filter = "Apps and shortcuts|*.exe;*.lnk|All files|*.*", CheckFileExists = true };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        var obj = new JsonObject { ["Name"] = Path.GetFileNameWithoutExtension(picker.FileName), ["Kind"] = "Command", ["FileName"] = picker.FileName, ["Arguments"] = "", ["WorkingDirectory"] = Path.GetDirectoryName(picker.FileName), ["Enabled"] = true, ["WaitMode"] = Path.GetExtension(picker.FileName).Equals(".exe", StringComparison.OrdinalIgnoreCase) ? "Process" : "Launch", ["ProcessName"] = Path.GetFileNameWithoutExtension(picker.FileName), ["TimeoutSeconds"] = 60, ["DelaySeconds"] = 2 };
        if (EditDialog(obj)) { AddRow(obj); SetDirty(); }
    }
    void EditSelected()
    {
        if (grid.SelectedRows.Count == 0) return;
        var row = grid.SelectedRows[0]; var obj = ((JsonObject)row.Tag!).DeepClone().AsObject();
        if (EditDialog(obj)) { row.Tag = obj; row.Cells[1].Value = S(obj, "Name"); row.Cells[2].Value = Details(obj); row.Cells[3].Value = S(obj, "WaitMode"); SetDirty(); }
    }
    bool EditDialog(JsonObject obj)
    {
        using var dialog = new Form { Text = "App launch and readiness", Width = 800, Height = 670, StartPosition = FormStartPosition.CenterParent, Font = Font, MinimizeBox = false, MaximizeBox = false, FormBorderStyle = FormBorderStyle.FixedDialog };
        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 2, AutoScroll = true };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 185)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var textInputs = new Dictionary<string, TextBox>(); int row = 0;
        void TextField(string key, string label, bool editable = true)
        {
            var input = new TextBox { Text = S(obj, key), Dock = DockStyle.Fill, ReadOnly = !editable };
            textInputs[key] = input;
            fields.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 7, 0, 0) }, 0, row);
            fields.Controls.Add(input, 1, row++);
        }
        bool command = S(obj, "Kind") == "Command";
        TextField("Name", "Name"); TextField("FileName", command ? "App path" : "Launch (registered)", command);
        if (!command) textInputs["FileName"].Text = Details(obj);
        TextField("Arguments", "Arguments", command); TextField("WorkingDirectory", "Start in", command);
        var mode = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        string[] modes = ["Launch", "Process", "Responsive", "Exit", "PortFile"];
        mode.Items.AddRange(["Launch accepted + delay", "Process is running", "Process is responsive", "Process exits successfully", "Fresh local server port file"]);
        mode.SelectedIndex = Math.Max(0, Array.IndexOf(modes, S(obj, "WaitMode")));
        fields.Controls.Add(new Label { Text = "Before next group", AutoSize = true }, 0, row); fields.Controls.Add(mode, 1, row++);
        TextField("ProcessName", "Process name (no .exe)"); TextField("ServiceName", "Required service (optional)"); TextField("PortFile", "JSON port file (optional)");
        var numbers = new Dictionary<string, NumericUpDown>();
        void NumberField(string key, string label, decimal fallback, decimal minimum)
        {
            var input = new NumericUpDown { Minimum = minimum, Maximum = 3600, Value = Math.Clamp(obj[key]?.GetValue<decimal>() ?? fallback, minimum, 3600), Dock = DockStyle.Left, Width = 100 };
            numbers[key] = input;
            fields.Controls.Add(new Label { Text = label, AutoSize = true }, 0, row); fields.Controls.Add(input, 1, row++);
        }
        NumberField("TimeoutSeconds", "Timeout (seconds)", 60, 1); NumberField("StableSeconds", "Stable for (seconds)", 0, 0); NumberField("DelaySeconds", "Extra delay (seconds)", 2, 0);
        var note = new Label { Text = "For launchers such as Update.exe, use the final app's process name. Launch accepted only confirms the launch request. Exit checks are for helper executables. Local server checks read a JSON file with a port property.", AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(0, 12, 0, 0) };
        fields.Controls.Add(note, 1, row++);
        var done = new RoundedButton { Text = "Done", Dock = DockStyle.Bottom, Height = 42 };
        done.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(textInputs["Name"].Text)) return;
            if (command && (!Path.IsPathFullyQualified(textInputs["FileName"].Text) || !File.Exists(textInputs["FileName"].Text))) { MessageBox.Show(dialog, "Choose an existing app with an absolute path."); return; }
            if (mode.SelectedIndex is 1 or 2 or 4 && string.IsNullOrWhiteSpace(textInputs["ProcessName"].Text)) { MessageBox.Show(dialog, "Enter the process name for this readiness check."); return; }
            if (mode.SelectedIndex == 3 && (!command || !textInputs["FileName"].Text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))) { MessageBox.Show(dialog, "Exit checks require an executable."); return; }
            if (mode.SelectedIndex == 4 && string.IsNullOrWhiteSpace(textInputs["PortFile"].Text)) { MessageBox.Show(dialog, "Enter a JSON port file path."); return; }
            if (numbers["TimeoutSeconds"].Value <= numbers["StableSeconds"].Value + numbers["DelaySeconds"].Value) { MessageBox.Show(dialog, "Timeout must exceed the stable time plus extra delay."); return; }
            dialog.DialogResult = DialogResult.OK;
        };
        dialog.Controls.Add(fields); dialog.Controls.Add(done); dialog.AcceptButton = done;
        ThemeDialog(dialog);
        if (dialog.ShowDialog(this) != DialogResult.OK) return false;
        foreach (var pair in textInputs) if (command || pair.Key is "Name" or "ProcessName" or "ServiceName" or "PortFile") obj[pair.Key] = pair.Value.Text.Trim();
        string processName = S(obj, "ProcessName"); if (processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) obj["ProcessName"] = processName[..^4];
        foreach (var pair in numbers) obj[pair.Key] = pair.Value.Value;
        obj["WaitMode"] = modes[mode.SelectedIndex];
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
        string text = await RunBackend("--state");
        var state = JsonNode.Parse(text)!.AsObject();
        UpdateHealthState(state);
        audio.Text = $"Startup task: {S(state, "Task")}     Saved groups: {state["Groups"]}     Apps: {state["Apps"]}     Last task result: {state["LastResult"]}";
        var problems = state["Conflicts"]?.AsArray();
        conflicts.ForeColor = problems?.Count > 0 ? WarningColor : MutedColor;
        conflicts.Text = problems?.Count > 0 ? $"{problems.Count} independent startup conflict(s): {string.Join(", ", problems.Select(n => n!.GetValue<string>()))}. Click Install / repair sequence." : "Managed original startup entries: disabled · no conflicts detected.";
        if (!dirty) status.Text = state["NeedsRepair"]?.GetValue<bool>() == true
            ? "The sign-in task uses the previous runner. Click Install / repair sequence to switch it to .NET."
            : S(state, "Task") == "Not installed" ? "The sign-in sequence is not installed. Click Install / repair sequence to enable it." : "Saved configuration · Windows runs this sequence without the manager being open.";
    }
    async Task InstallSequence()
    {
        if (!SaveBeforeAction()) return;
        if (MessageBox.Show(this, "Install or repair the sign-in sequence using the saved configuration? Windows will ask for administrator access.", "Install startup sequence", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        await RunElevated("--install"); await RefreshReadiness();
        status.Text = "Startup sequence installed. It will run at your next sign-in.";
    }
    async Task ImportApps()
    {
        if (selectedGroup == null) { MessageBox.Show(this, "Create or select a group first."); return; }
        if (!SaveBeforeAction()) return;
        var candidates = JsonNode.Parse(await RunBackend("--candidates"))!.AsArray();
        if (candidates.Count == 0) { MessageBox.Show(this, "No additional enabled desktop startup apps were found. Previously disabled apps are left alone.", "Startup entries"); return; }
        using var dialog = new Form { Text = "Import enabled startup apps", Width = 840, Height = 500, StartPosition = FormStartPosition.CenterParent, Font = Font };
        var list = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, HorizontalScrollbar = true };
        foreach (var node in candidates) { var candidate = node!.AsObject(); list.Items.Add(S(candidate, "Name") + "  —  " + S(candidate, "Origin")); }
        var note = new Label { Text = $"Selected entries will stop launching independently and join {S(selectedGroup, "Name")}.\nWindows will ask for administrator access. System services and security startup are excluded.", Dock = DockStyle.Top, Height = 65, Padding = new Padding(10) };
        var button = new RoundedButton { Text = "Import selected", Dock = DockStyle.Bottom, Height = 42 };
        button.Click += (_, _) => { if (list.CheckedIndices.Count > 0) dialog.DialogResult = DialogResult.OK; };
        dialog.Controls.Add(list); dialog.Controls.Add(note); dialog.Controls.Add(button);
        ThemeDialog(dialog);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var ids = new JsonArray(); foreach (int i in list.CheckedIndices) ids.Add(S(candidates[i]!.AsObject(), "Id"));
        File.WriteAllText(Path.Combine(Runtime, "pending-import.json"), new JsonObject { ["GroupId"] = S(selectedGroup, "Id"), ["Ids"] = ids }.ToJsonString());
        string resultPath = Path.Combine(Runtime, "import-result.json"); if (File.Exists(resultPath)) File.Delete(resultPath);
        await RunElevated("--import");
        if (!File.Exists(resultPath)) throw new IOException("The import did not complete. The current configuration was left in place.");
        var result = JsonNode.Parse(File.ReadAllText(resultPath))!.AsObject();
        if (!result["Success"]!.GetValue<bool>()) throw new IOException(S(result, "Error"));
        LoadConfig(); await RefreshReadiness(); status.Text = $"Imported {result["Count"]} startup app(s) into the selected group.";
    }
    async Task Restore()
    {
        if (MessageBox.Show(this, "Restore the captured original startup settings and remove the ordered startup task? Windows will ask for administrator access.", "Restore original startup", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        await RunElevated("--restore"); dirty = false;
        MessageBox.Show(this, "Original startup settings restored. The custom startup task has been removed.", "Restored");
        busy = false; Close();
    }
    ProcessStartInfo BackendInfo(string command, bool elevated = false)
    {
        var info = new ProcessStartInfo(AppPaths.Executable) { UseShellExecute = elevated, CreateNoWindow = !elevated, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var arg in new[] { command, "--state-directory", Runtime }) info.ArgumentList.Add(arg);
        if (elevated) info.Verb = "runas";
        else { info.RedirectStandardOutput = true; info.RedirectStandardError = true; info.StandardOutputEncoding = System.Text.Encoding.UTF8; info.StandardErrorEncoding = System.Text.Encoding.UTF8; }
        return info;
    }
    async Task<string> RunBackend(string command)
    {
        var info = BackendInfo(command);
        using var process = Process.Start(info) ?? throw new IOException("Could not start the startup helper.");
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        try { await process.WaitForExitAsync(timeout.Token); } catch (OperationCanceledException) { process.Kill(true); throw new TimeoutException("The startup check timed out. Your settings were not changed."); }
        string text = await output; string error = await errors;
        if (process.ExitCode != 0) throw new IOException(string.IsNullOrWhiteSpace(error) ? "The startup helper failed." : error);
        return text;
    }
    async Task RunElevated(string command)
    {
        using var process = Process.Start(BackendInfo(command, true)) ?? throw new IOException("Administrator access was not granted.");
        await process.WaitForExitAsync(); if (process.ExitCode != 0) throw new IOException("The change did not complete. See backend-error.log in " + Runtime + " for details.");
    }
    void Guard(Action action) { try { action(); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Startup Manager", MessageBoxButtons.OK, MessageBoxIcon.Error); } }
    async Task GuardAsync(Func<Task> action)
    {
        if (busy) return; busy = true; actions.Enabled = false; grid.Enabled = false; groupList.Enabled = false; groupActions.Enabled = false; board.Enabled = false; statusBar.Enabled = false;
        try { await action(); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Startup Manager", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { busy = false; actions.Enabled = true; grid.Enabled = true; groupList.Enabled = true; groupActions.Enabled = true; board.Enabled = true; statusBar.Enabled = true; }
    }
}

