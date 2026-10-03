using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace StartupManager;

sealed partial class ManagerForm
{
    static readonly Color CanvasColor = Color.FromArgb(18, 20, 26);
    static readonly Color SurfaceColor = Color.FromArgb(28, 31, 40);
    static readonly Color TileColor = Color.FromArgb(37, 41, 53);
    static readonly Color BorderColor = Color.FromArgb(53, 59, 74);
    static readonly Color TextColor = Color.FromArgb(235, 238, 247);
    static readonly Color MutedColor = Color.FromArgb(154, 164, 184);
    static readonly Color AccentColor = Color.FromArgb(164, 151, 255);
    static readonly Color WarningColor = Color.FromArgb(255, 171, 133);
    readonly ResponsiveBoard board = new() { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 4) };
    readonly ToolTip hints = new() { AutoPopDelay = 10000 };
    readonly AppIconProvider appIcons = new();
    bool boardRefreshPending;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int dark = 1;
        DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
    }

    void BuildBoardShell()
    {
        Text = "Startup Manager";
        Font = new Font("Segoe UI", 10);
        BackColor = CanvasColor; ForeColor = TextColor;
        MinimumSize = new Size(840, 620); Size = new Size(1320, 820);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.Control && e.KeyCode == Keys.S && !busy) { Guard(Save); e.SuppressKeyPress = true; } };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28, 22, 28, 18), ColumnCount = 1, RowCount = 4 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 4; i++) layout.RowStyles.Add(new RowStyle(i == 2 ? SizeType.Percent : SizeType.AutoSize, i == 2 ? 100 : 0));

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = Padding.Empty };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var appName = Caption("Startup Manager", 20, TextColor, true);
        appName.Anchor = AnchorStyles.Left; header.Controls.Add(appName, 0, 0);
        actions.AutoSize = true; actions.Dock = DockStyle.None; actions.WrapContents = false; actions.Margin = new Padding(12, 0, 0, 0);
        var save = ActionButton("Save changes", () => Guard(Save), true); hints.SetToolTip(save, "Save configuration (Ctrl+S)"); actions.Controls.Add(save);
        var tools = ActionButton("Sequence tools  ▾", () => { });
        var toolsMenu = NewMenu();
        toolsMenu.Items.Add("Check readiness", null, async (_, _) => await GuardAsync(RefreshReadiness));
        toolsMenu.Items.Add("Install / repair sequence…", null, async (_, _) => await GuardAsync(InstallSequence));
        toolsMenu.Items.Add("View startup log", null, (_, _) => Guard(() => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", Path.Combine(Runtime, "startup.log")) { UseShellExecute = true })));
        toolsMenu.Items.Add(new ToolStripSeparator());
        toolsMenu.Items.Add("Restore original startup…", null, async (_, _) => await GuardAsync(Restore));
        tools.Click += (_, _) => toolsMenu.Show(tools, new Point(0, tools.Height));
        tools.Disposed += (_, _) => toolsMenu.Dispose(); actions.Controls.Add(tools);
        header.Controls.Add(actions, 1, 0); layout.Controls.Add(header, 0, 0);
        var sequenceHeading = Caption("Startup Sequence", 13, TextColor, true);
        sequenceHeading.Margin = new Padding(0, 28, 0, 12);
        layout.Controls.Add(sequenceHeading, 0, 1); layout.Controls.Add(board, 0, 2);
        layout.Controls.Add(BuildStatusBar(), 0, 3);
        Controls.Add(layout);
        // Existing editing and persistence use this off-screen buffer for the active group.
        grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Enabled" });
        foreach (string name in new[] { "Name", "Launch", "Wait" }) grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name });
        groupList.SelectedIndexChanged += (_, _) => { if (!loading) { CommitGroupRows(); ShowSelectedGroup(); } };
        FormClosed += (_, _) => { appIcons.Dispose(); hints.Dispose(); grid.Dispose(); groupList.Dispose(); groupActions.Dispose(); };
    }

    static Label Caption(string text, float size, Color color, bool bold = false) => new()
    {
        Text = text, AutoSize = true, ForeColor = color, BackColor = Color.Transparent,
        Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular), Margin = Padding.Empty
    };

    static Button ActionButton(string text, Action action, bool primary = false)
    {
        var button = new RoundedButton { Text = text, AutoSize = true, MinimumSize = new Size(0, 36), Padding = new Padding(10, 4, 10, 4), Margin = new Padding(0, 0, 8, 0), FlatStyle = FlatStyle.Flat, BackColor = primary ? AccentColor : TileColor, ForeColor = primary ? CanvasColor : TextColor, Cursor = Cursors.Hand };
        button.FlatAppearance.BorderSize = primary ? 0 : 1; button.FlatAppearance.BorderColor = BorderColor;
        button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(185, 174, 255) : Color.FromArgb(49, 54, 69);
        button.Click += (_, _) => action(); return button;
    }

    static ContextMenuStrip NewMenu() => new() { BackColor = TileColor, ForeColor = TextColor, ShowImageMargin = false, Renderer = new DarkMenuRenderer() };

    void SelectBoardGroup(JsonObject group, int? appIndex = null)
    {
        if (selectedGroup != group)
        {
            CommitGroupRows();
            loading = true; groupList.SelectedIndex = Groups.IndexOf(group); loading = false;
            selectedGroup = group;
            loading = true; grid.Rows.Clear();
            foreach (var node in group["Apps"]!.AsArray()) AddRow(node!.AsObject().DeepClone().AsObject());
            loading = false;
        }
        if (appIndex is int index && index < grid.Rows.Count)
        {
            grid.ClearSelection(); grid.CurrentCell = grid.Rows[index].Cells[1]; grid.Rows[index].Selected = true;
        }
    }

    void QueueBoardRefresh()
    {
        if (boardRefreshPending || !IsHandleCreated || IsDisposed) return;
        boardRefreshPending = true;
        BeginInvoke((Action)(() => { boardRefreshPending = false; if (!IsDisposed) { CommitGroupRows(); RenderBoard(); } }));
    }

    void RenderBoard()
    {
        if (configuration["Groups"] is not JsonArray) return;
        int scroll = board.ScrollOffset;
        var appScrolls = board.Cards.SelectMany(card => card.Controls.OfType<AppList>()).ToDictionary(list => (string)list.Tag!, list => list.ScrollOffset);
        board.SuspendLayout();
        foreach (Control control in board.Cards.ToArray()) control.Dispose();
        int position = 0;
        foreach (var node in Groups)
        {
            var group = node!.AsObject(); position++;
            var apps = group["Apps"]!.AsArray();
            var card = new RoundedPanel { Width = 332, Height = 462, BackColor = SurfaceColor, Margin = new Padding(0, 0, 18, 0), Padding = new Padding(16), BorderColor = BorderColor };
            var header = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 3, Padding = new Padding(0, 0, 0, 14) };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
            for (int row = 0; row < 3; row++) header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var step = Caption($"GROUP {position:00}", 9, AccentColor, true); step.Anchor = AnchorStyles.Left; header.Controls.Add(step, 0, 0);
            var title = Caption(S(group, "Name"), 16, TextColor, true); title.MaximumSize = new Size(294, 0); title.Margin = new Padding(0, 6, 0, 0); hints.SetToolTip(title, S(group, "Name")); header.Controls.Add(title, 0, 1); header.SetColumnSpan(title, 2);
            var menuButton = ActionButton("…", () => { }); menuButton.AutoSize = false; menuButton.Size = new Size(36, 36); menuButton.MinimumSize = new Size(36, 36); menuButton.Padding = Padding.Empty; menuButton.Margin = Padding.Empty; menuButton.Anchor = AnchorStyles.Top | AnchorStyles.Right; ((RoundedButton)menuButton).DrawEllipsis = true; menuButton.AccessibleName = $"Options for {S(group, "Name")}"; header.Controls.Add(menuButton, 1, 0);
            var menu = NewMenu();
            void GroupAction(string text, Action action, bool enabled = true) { var item = menu.Items.Add(text, null, (_, _) => Guard(() => { SelectBoardGroup(group); action(); })); item.Enabled = enabled; }
            GroupAction("Edit group…", EditGroup);
            GroupAction(IsEnabled(group) ? "Disable group" : "Enable group", () => { group["Enabled"] = !IsEnabled(group); SetDirty(); });
            GroupAction("Move left", () => MoveGroup(-1), position > 1); GroupAction("Move right", () => MoveGroup(1), position < Groups.Count);
            menu.Items.Add(new ToolStripSeparator()); GroupAction("Delete group…", DeleteGroup);
            menuButton.Click += (_, _) => menu.Show(menuButton, new Point(0, menuButton.Height)); card.Disposed += (_, _) => menu.Dispose();
            var behavior = Caption($"{apps.Count} app{(apps.Count == 1 ? "" : "s")} · {(S(group, "OnFailure") == "Continue" ? "Continue on failure" : "Stop on failure")}" + ((group["DelayAfterSeconds"]?.GetValue<decimal>() ?? 0) > 0 ? $" · +{group["DelayAfterSeconds"]}s" : "") + (IsEnabled(group) ? "" : " · Disabled"), 8.5f, MutedColor);
            behavior.MaximumSize = new Size(294, 0); behavior.Margin = new Padding(0, 7, 0, 0); header.Controls.Add(behavior, 0, 2); header.SetColumnSpan(behavior, 2);
            card.SizeChanged += (_, _) => { int width = Math.Max(1, card.ClientSize.Width - card.Padding.Horizontal); title.MaximumSize = behavior.MaximumSize = new Size(width, 0); };
            var list = new AppList { Dock = DockStyle.Fill, Tag = S(group, "Id") };
            if (apps.Count == 0)
            {
                var empty = Caption("Add an app or import startup entries.", 10, MutedColor);
                empty.MaximumSize = new Size(278, 0); empty.Margin = new Padding(6, 22, 6, 0); list.AddItem(empty);
            }
            for (int i = 0; i < apps.Count; i++) list.AddItem(BuildAppTile(group, apps[i]!.AsObject(), i, apps.Count));
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(0, 12, 0, 0) };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            buttons.Controls.Add(ActionButton("+ Add app", () => Guard(() => { SelectBoardGroup(group); AddApp(); })));
            var import = ActionButton("Import…", () => { }); import.Click += async (_, _) => { SelectBoardGroup(group); await GuardAsync(ImportApps); }; buttons.Controls.Add(import);
            footer.Controls.Add(buttons);
            card.Controls.Add(list); card.Controls.Add(footer); card.Controls.Add(header); board.Controls.Add(card);
            list.ScrollOffset = appScrolls.GetValueOrDefault(S(group, "Id"));
        }
        var add = new RoundedPanel { Width = Groups.Count == 0 ? 332 : 164, Height = 462, BackColor = CanvasColor, BorderColor = BorderColor, Margin = new Padding(0, 0, 8, 0), Padding = new Padding(16) };
        var addButton = ActionButton("+", () => Guard(AddGroup)); addButton.AutoSize = false; addButton.Size = new Size(48, 48); addButton.MinimumSize = new Size(48, 48); addButton.Padding = Padding.Empty; addButton.Margin = Padding.Empty; addButton.AccessibleName = "New group"; ((RoundedButton)addButton).DrawPlus = true; hints.SetToolTip(addButton, "New group");
        void CenterAddButton() => addButton.Location = new Point((add.ClientSize.Width - addButton.Width) / 2, (add.ClientSize.Height - addButton.Height) / 2);
        add.Controls.Add(addButton); add.SizeChanged += (_, _) => CenterAddButton(); CenterAddButton(); board.Controls.Add(add);
        board.ResumeLayout(true); board.ScrollOffset = scroll;
    }

    Control BuildAppTile(JsonObject group, JsonObject app, int index, int count)
    {
        var tile = new AppIconTile { Text = DisplayName(app), Dimmed = !IsEnabled(app) || !IsEnabled(group), AccessibleName = DisplayName(app), AccessibleDescription = !IsEnabled(app) ? "Disabled" : !IsEnabled(group) ? "Group disabled" : "Enabled", AccessibleRole = AccessibleRole.PushButton };
        hints.SetToolTip(tile, $"{DisplayName(app)}\n{(!IsEnabled(app) ? "Disabled · " : !IsEnabled(group) ? "Group disabled · " : "")}Wait: {WaitLabel(S(app, "WaitMode"))}\n{Details(app)}");
        tile.HandleCreated += async (_, _) =>
        {
            var icon = await appIcons.GetAsync(app.DeepClone().AsObject(), DisplayName(app));
            if (!tile.IsDisposed) { tile.IconImage = icon; tile.Invalidate(); }
        };
        var menu = NewMenu();
        void AppAction(string text, Action action, bool active = true) { var item = menu.Items.Add(text, null, (_, _) => Guard(() => { SelectBoardGroup(group, index); action(); })); item.Enabled = active; }
        AppAction(IsEnabled(app) ? "Disable app" : "Enable app", () => { grid.Rows[index].Cells[0].Value = !IsEnabled(app); SetDirty(); });
        AppAction("Edit app…", EditSelected); AppAction("Move earlier", () => MoveSelected(-1), index > 0); AppAction("Move later", () => MoveSelected(1), index < count - 1); AppAction("Move to group…", MoveAppToGroup, Groups.Count > 1);
        menu.Items.Add(new ToolStripSeparator()); AppAction("Remove app…", RemoveSelected);
        var more = tile.MenuButton;
        more.Click += (_, _) => menu.Show(more, new Point(0, more.Height));
        tile.ContextMenuStrip = menu; tile.Disposed += (_, _) => menu.Dispose();
        tile.DoubleClick += (_, _) => Guard(() => { SelectBoardGroup(group, index); EditSelected(); });
        tile.KeyDown += (_, e) => { if (e.KeyCode is Keys.Enter or Keys.Space or Keys.Apps) { menu.Show(tile, new Point(0, tile.Height)); e.Handled = true; } };
        return tile;
    }

    static string WaitLabel(string mode) => mode switch { "Process" => "Process running", "Responsive" => "Responsive", "Exit" => "Successful exit", "PortFile" => "Local server", _ => "Launch + delay" };

    static void ThemeDialog(Form dialog)
    {
        dialog.BackColor = SurfaceColor; dialog.ForeColor = TextColor;
        void Theme(Control parent)
        {
            foreach (Control control in parent.Controls)
            {
                control.ForeColor = TextColor;
                control.BackColor = control is TextBoxBase or ComboBox or NumericUpDown or ListBox ? TileColor : SurfaceColor;
                if (control is Button button) { button.FlatStyle = FlatStyle.Flat; button.BackColor = TileColor; button.FlatAppearance.BorderColor = BorderColor; }
                if (control is TextBox text) text.BorderStyle = BorderStyle.FixedSingle;
                Theme(control);
            }
        }
        Theme(dialog);
        dialog.HandleCreated += (_, _) => { int dark = 1; DwmSetWindowAttribute(dialog.Handle, 20, ref dark, sizeof(int)); };
    }

    sealed class RoundedPanel : Panel
    {
        public Color BorderColor { get; init; }
        public RoundedPanel() { DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw, true); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = new GraphicsPath();
            const int diameter = 20;
            var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90); path.CloseFigure();
            using var brush = new SolidBrush(BackColor); e.Graphics.Clear(Parent?.BackColor ?? CanvasColor); e.Graphics.FillPath(brush, path);
            using var pen = new Pen(BorderColor); e.Graphics.DrawPath(pen, path);
        }
    }

    sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkMenuColors()) { RoundedEdges = false; }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) { e.TextColor = e.Item.Enabled ? TextColor : MutedColor; base.OnRenderItemText(e); }
    }
    sealed class DarkMenuColors : ProfessionalColorTable
    {
        public override Color MenuItemSelected => BorderColor;
        public override Color MenuItemBorder => AccentColor;
        public override Color ToolStripDropDownBackground => TileColor;
        public override Color MenuBorder => BorderColor;
        public override Color SeparatorDark => BorderColor;
        public override Color SeparatorLight => BorderColor;
    }
}
