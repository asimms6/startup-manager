using System.Text.Json.Nodes;

namespace StartupManager;

sealed partial class ManagerForm
{
    static readonly Color ReadyColor = Color.FromArgb(126, 208, 169);
    readonly RoundedPanel statusBar = new() { Dock = DockStyle.Fill, Height = 64, BackColor = SurfaceColor, BorderColor = BorderColor, Padding = new Padding(14, 12, 10, 12), Margin = new Padding(0, 12, 0, 0) };
    readonly StatusBadge signInBadge = new(), conflictBadge = new(), savedBadge = new();
    Button? setupButton;
    string taskState = "Checking";
    int? conflictCount;
    bool healthCheckFailed;
    bool needsRepair;

    Control BuildStatusBar()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var badges = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        badges.Controls.AddRange([signInBadge, conflictBadge, savedBadge]); layout.Controls.Add(badges, 0, 0);
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        setupButton = ActionButton("Enable sign-in…", () => { });
        setupButton.Click += async (_, _) => await GuardAsync(InstallSequence); buttons.Controls.Add(setupButton);
        var details = ActionButton("Details", ShowStatusDetails); details.Margin = Padding.Empty; buttons.Controls.Add(details);
        layout.Controls.Add(buttons, 1, 0); statusBar.Controls.Add(layout);
        status.TextChanged += (_, _) => UpdateStatusBar();
        conflicts.TextChanged += (_, _) =>
        {
            if (conflicts.Text.StartsWith("Startup check failed:", StringComparison.Ordinal)) { healthCheckFailed = true; UpdateStatusBar(); }
        };
        UpdateStatusBar(); return statusBar;
    }

    void UpdateHealthState(JsonObject state)
    {
        taskState = S(state, "Task"); conflictCount = state["Conflicts"]?.AsArray().Count ?? 0;
        needsRepair = state["NeedsRepair"]?.GetValue<bool>() ?? false;
        healthCheckFailed = false; UpdateStatusBar();
    }

    void UpdateStatusBar()
    {
        var signIn = healthCheckFailed ? ("Check failed", WarningColor) : needsRepair ? ("Repair needed", WarningColor) : taskState switch
        {
            "Not installed" => ("Sign-in off", WarningColor),
            "Disabled" => ("Sign-in disabled", WarningColor),
            "Running" => ("Sequence running", AccentColor),
            "Ready" => ("Sign-in ready", ReadyColor),
            "Checking" => ("Checking sign-in", MutedColor),
            _ => ("Sign-in: " + taskState, MutedColor)
        };
        signInBadge.SetStatus(signIn.Item1, signIn.Item2);
        conflictBadge.SetStatus(healthCheckFailed || conflictCount == null ? "Not checked" : conflictCount == 0 ? "No conflicts" : $"{conflictCount} conflict{(conflictCount == 1 ? "" : "s")}", healthCheckFailed || conflictCount == null ? MutedColor : conflictCount == 0 ? ReadyColor : WarningColor);
        savedBadge.SetStatus(dirty ? "Unsaved changes" : "Saved", dirty ? WarningColor : MutedColor);
        hints.SetToolTip(signInBadge, taskState == "Not installed" ? "Enable sign-in to run this sequence automatically when you log in." : audio.Text);
        hints.SetToolTip(conflictBadge, conflicts.Text);
        hints.SetToolTip(savedBadge, status.Text);
        if (setupButton != null)
        {
            setupButton.Text = needsRepair || conflictCount > 0 ? "Repair sequence…" : "Enable sign-in…";
            setupButton.Visible = !healthCheckFailed && (taskState is "Not installed" or "Disabled" || conflictCount > 0 || needsRepair);
        }
    }

    void ShowStatusDetails()
    {
        using var dialog = new Form { Text = "Sequence status", Size = new Size(680, 340), StartPosition = FormStartPosition.CenterParent, Font = Font, MinimizeBox = false, MaximizeBox = false, FormBorderStyle = FormBorderStyle.FixedDialog };
        var text = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Text = $"{audio.Text}\r\n\r\n{conflicts.Text}\r\n\r\n{status.Text}", BorderStyle = BorderStyle.None };
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20) }; panel.Controls.Add(text);
        var close = ActionButton("Close", () => dialog.Close()); close.Dock = DockStyle.Bottom; dialog.Controls.Add(panel); dialog.Controls.Add(close);
        ThemeDialog(dialog); dialog.AcceptButton = close; dialog.CancelButton = close; dialog.ShowDialog(this);
    }

    sealed class StatusBadge : Control
    {
        Color dotColor;
        public StatusBadge() { Height = 36; Margin = new Padding(0, 0, 20, 0); Font = new Font("Segoe UI", 9); SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true); AccessibleRole = AccessibleRole.StaticText; }
        public void SetStatus(string text, Color color) { Text = text; dotColor = color; Width = TextRenderer.MeasureText(Text, Font).Width + 22; AccessibleName = text; Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(SurfaceColor); e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(dotColor); float diameter = 6 * DeviceDpi / 96f;
            e.Graphics.FillEllipse(brush, 1, (Height - diameter) / 2, diameter, diameter);
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(16, 0, Width - 16, Height), TextColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }
}
