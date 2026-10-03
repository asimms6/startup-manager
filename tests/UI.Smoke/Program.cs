using System.Reflection;
using System.Text.Json.Nodes;

// Exercises the board against a disposable configuration beside this test build.
// No helper scripts, scheduled tasks, or installed user configuration are touched.
static class BoardSmoke
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        var type = Assembly.Load("StartupManager").GetType("StartupManager.ManagerForm")!;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        string runtime = Path.Combine(AppContext.BaseDirectory, "fixture-state");
        Directory.CreateDirectory(runtime);
        string configPath = Path.Combine(runtime, "config.json");
        var fixture = new JsonObject
        {
            ["SchemaVersion"] = 2,
            ["UserSid"] = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value,
            ["TaskName"] = "UI smoke fixture",
            ["Groups"] = new JsonArray(Group("audio", "Audio", "First", "Second"), Group("work", "Work", "Third"))
        };
        File.WriteAllText(configPath, fixture.ToJsonString());
        File.WriteAllText(Path.Combine(runtime, "backup.json"), "{\"Registry\":[],\"Tasks\":[]}");
        using var form = (Form)Activator.CreateInstance(type, new object[] { runtime })!;
        object? Call(string name, params object?[] args) => type.GetMethod(name, flags)!.Invoke(form, args);
        T Field<T>(string name) => (T)type.GetField(name, flags)!.GetValue(form)!;
        void Assert(bool value, string message) { if (!value) throw new Exception(message); }
        Call("LoadConfig");
        var shownField = typeof(Form).GetFields(BindingFlags.Static | BindingFlags.NonPublic).Single(field => field.Name.Contains("shown", StringComparison.OrdinalIgnoreCase)); var shownKey = shownField.GetValue(null)!;
        var events = (System.ComponentModel.EventHandlerList)typeof(System.ComponentModel.Component).GetProperty("Events", flags)!.GetValue(form)!;
        events.RemoveHandler(shownKey, events[shownKey]);
        form.Show(); Application.DoEvents();
        Call("UpdateHealthState", new JsonObject { ["Task"] = "Not installed", ["Conflicts"] = new JsonArray() });
        Assert(Field<Control>("signInBadge").Text == "Sign-in off", "Missing installation must be visible in the status bar.");
        Assert(Field<Control>("conflictBadge").Text == "No conflicts", "Conflict status is incorrect.");
        var board = Field<Panel>("board");
        Control[] Cards() => board.Controls.Cast<Control>().Where(control => control.GetType().Name == "RoundedPanel").ToArray();
        Assert(Cards().Length == 3, "Expected two group cards and an add-group card.");
        foreach (var size in new[] { new Size(840, 620), new Size(1320, 820), new Size(1920, 620), new Size(1920, 1080), new Size(840, 820) })
        {
            form.Size = size; form.PerformLayout(); board.PerformLayout(); Application.DoEvents();
            var cards = Cards();
            foreach (var card in cards)
            foreach (Control list in card.Controls.Cast<Control>().Where(c => c.GetType().Name == "AppList"))
            {
                list.PerformLayout();
                foreach (Control content in list.Controls)
                foreach (Control tile in content.Controls)
                    Assert(tile.Visible && tile.Width > 0 && tile.Height > 0 && tile.Top < content.Height && content.Top < list.Height, "Resizing hid an app icon.");
            }
            Assert(cards.All(card => card.Top >= 0 && card.Bottom <= board.ClientSize.Height), "Resizing clipped group controls vertically.");
            Assert(!board.VerticalScroll.Visible && !board.HorizontalScroll.Visible, "Board should never show native light scrollbars.");
            Assert(Field<Control>("statusBar").Bottom <= form.ClientSize.Height, "Resizing clipped the status bar.");
            if (size.Width == 1920) Assert(cards[0].Width > 332 && cards[^1].Right <= board.ClientSize.Width, "Wide window did not expand cards to fill available width.");
        }
        using (var render = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(render, new Rectangle(Point.Empty, render.Size)); render.Save(Path.Combine(runtime, "resize-check.png")); }
        var config = Field<JsonObject>("configuration");
        var groups = config["Groups"]!.AsArray();
        Call("SelectBoardGroup", groups[0]!.AsObject(), 0);
        var grid = Field<DataGridView>("grid");
        Assert(grid.SelectedRows.Count == 1, "Card actions must select the off-screen app row.");
        grid.Rows[0].Cells[0].Value = false;
        Call("SetDirty"); Call("CommitGroupRows");
        Assert(Field<Control>("savedBadge").Text == "Unsaved changes", "Dirty state is missing from the status bar.");
        Assert(!groups[0]!["Apps"]![0]!["Enabled"]!.GetValue<bool>(), "App toggle was lost.");
        Call("MoveSelected", 1); Call("CommitGroupRows");
        Assert(groups[0]!["Apps"]![1]!["Name"]!.GetValue<string>() == "First", "App ordering was lost.");
        Call("SelectBoardGroup", groups[1]!.AsObject(), 0);
        grid.Rows[0].Cells[0].Value = false; Call("CommitGroupRows");
        Call("MoveGroup", -1);
        Assert(groups[0]!["Id"]!.GetValue<string>() == "work", "Group ordering was lost.");
        Call("Save");
        Assert(Field<Control>("savedBadge").Text == "Saved", "Status bar did not reset after saving.");
        Call("UpdateHealthState", new JsonObject { ["Task"] = "Ready", ["Conflicts"] = new JsonArray("Fixture conflict") });
        Assert(Field<Control>("signInBadge").Text == "Sign-in ready" && Field<Control>("conflictBadge").Text == "1 conflict", "Ready/conflict state is incorrect.");
        Assert(Field<Button>("setupButton").Text == "Repair sequence…", "Conflicts need a repair action.");
        Call("UpdateHealthState", new JsonObject { ["Task"] = "Ready", ["Conflicts"] = new JsonArray(), ["NeedsRepair"] = true });
        Assert(Field<Control>("signInBadge").Text == "Repair needed" && Field<Button>("setupButton").Visible, "Legacy runner migration must offer repair even without conflicts.");
        var saved = JsonNode.Parse(File.ReadAllText(configPath))!;
        Assert(saved["Groups"]![0]!["Id"]!.GetValue<string>() == "work", "Saved order differs from board.");
        Assert(!saved["Groups"]![0]!["Apps"]![0]!["Enabled"]!.GetValue<bool>(), "Cross-group toggle was lost on save.");
        Assert(!saved["Groups"]![1]!["Apps"]![1]!["Enabled"]!.GetValue<bool>(), "Earlier toggle was lost on switching groups.");
        Assert(saved["Groups"]![1]!["Apps"]![1]!["Arguments"]!.GetValue<string>() == "--fixture", "Launch arguments changed.");
        config = Field<JsonObject>("configuration");
        config["Groups"]!.AsArray().Clear(); Call("RebuildGroups", (object?)null);
        Assert(Cards().Length == 1, "Empty configuration must show the add-group card.");
        Assert(grid.Rows.Count == 0, "Empty board retained stale app rows.");
        Console.WriteLine("PASS: visible icons across window sizes, board rendering, app selection/toggles/order, group switching/order, save preservation, empty state, status transitions.");
    }

    static JsonObject Group(string id, string name, params string[] names) => new()
    {
        ["Id"] = id, ["Name"] = name, ["Enabled"] = true, ["OnFailure"] = "Stop", ["DelayAfterSeconds"] = 0,
        ["Apps"] = new JsonArray(names.Select(name => (JsonNode)new JsonObject
        {
            ["Name"] = name, ["Kind"] = "Command", ["FileName"] = @"C:\Windows\notepad.exe", ["Arguments"] = "--fixture", ["Enabled"] = true, ["WaitMode"] = "Process", ["ProcessName"] = "notepad", ["TimeoutSeconds"] = 60, ["StableSeconds"] = 0, ["DelaySeconds"] = 0
        }).ToArray())
    };
}
