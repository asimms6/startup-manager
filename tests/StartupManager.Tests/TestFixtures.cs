using System.Text.Json.Nodes;
using StartupManager.Core;
using static StartupManager.Core.Configuration;

namespace StartupManager.Tests;

sealed class FakeClock : IStartupClock
{
    public DateTimeOffset Now { get; private set; } = DateTimeOffset.Parse("2020-01-01T00:00:00Z");
    public void Sleep(TimeSpan duration) => Now += duration;
}
sealed class FakeLauncher(FakeClock clock) : IAppLauncher
{
    public List<string> Events { get; } = [];
    public Func<JsonObject, bool>? Ready
    {
        get; set;
    }
    public IDisposable? Launch(JsonObject app)
    {
        Events.Add("start:" + Text(app, "Name"));
        if (Text(app, "Name") == "Broken")
        {
            throw new IOException("Launch failed");
        }

        return null;
    }
    public bool IsReady(JsonObject app, IDisposable? launch)
    {
        Events.Add("check:" + Text(app, "Name"));
        if (Ready != null)
        {
            return Ready(app);
        }

        return Text(app, "Name") switch
        {
            "Never" => false,
            "Slow" => clock.Now.Second >= 1,
            _ => true
        };
    }
}
sealed class FakeSystem : IStartupSystem
{
    public string UserSid { get; set; } = "test-user";
    public bool IsAdministrator { get; set; } = true;
    public List<JsonObject> Available { get; } = [];
    public Dictionary<string, JsonObject> Registry { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ScheduledEntry> Tasks { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? FailDisable
    {
        get; set;
    }
    public string? FailRestore
    {
        get; set;
    }
    public List<string> Operations { get; } = [];
    public List<JsonObject> Candidates(bool includeDisabled = false) => Available.Where(c => includeDisabled ||
        Text(c, "SourceKind") == "Task" || RegistryConflict(new JsonObject
        {
            ["Path"] = Text(c, "SourcePath"),
            ["Name"] = Text(c, "SourceName"),
            ["Change"] = Text(c, "SourceKind") == "Package" ? "DisablePackage" : "DisableRun"
        })).Select(c => c.DeepClone().AsObject()).ToList();
    public JsonObject CaptureRegistry(string path, string name, string change)
    {
        if (Registry.TryGetValue(path + "|" + name, out var entry))
        {
            var copy = entry.DeepClone().AsObject();
            copy["Change"] = change;
            return copy;
        }
        return new JsonObject { ["Path"] = path, ["Name"] = name, ["Existed"] = false, ["Type"] = change == "DisableRun" ? "Binary" : "DWord", ["Change"] = change };
    }
    public void DisableRegistry(JsonObject record)
    {
        Operations.Add("disable:" + Text(record, "Name"));
        if (Text(record, "Name") == FailDisable)
        {
            throw new IOException("Cannot disable " + FailDisable);
        }

        var disabled = record.DeepClone().AsObject();
        disabled["Existed"] = true;
        disabled["Value"] = Text(record, "Change") == "DisableRun" ? JsonValue.Create(Convert.ToBase64String(new byte[] { 3 })) : JsonValue.Create(1);
        Registry[Text(record, "Path") + "|" + Text(record, "Name")] = disabled;
    }
    public void RestoreRegistry(JsonObject record)
    {
        Operations.Add("restore:" + Text(record, "Name"));
        if (Text(record, "Name") == FailRestore)
        {
            throw new IOException("Cannot restore " + FailRestore);
        }

        string id = Text(record, "Path") + "|" + Text(record, "Name");
        if (record["Existed"]!.GetValue<bool>())
        {
            Registry[id] = record.DeepClone().AsObject();
        }
        else
        {
            Registry.Remove(id);
        }
    }
    public bool RegistryConflict(JsonObject record)
    {
        if (!Registry.TryGetValue(Text(record, "Path") + "|" + Text(record, "Name"), out var entry))
        {
            return Text(record, "Change") == "DisableRun";
        }

        return Text(record, "Change") == "DisableRun" ? Convert.FromBase64String(Text(entry, "Value"))[0] is 2 or 6 : Number(entry, "Value") == 2;
    }
    public ScheduledEntry? FindTask(string name, string path = "\\") => Tasks.GetValueOrDefault(path + name);
    public void RegisterTask(string name, string path, string xml)
    {
        Operations.Add("task:" + name);
        Tasks[path + name] = new ScheduledEntry(name, path, xml, 3, 0);
    }
    public void DeleteTask(string name)
    {
        Operations.Add("delete:" + name);
        Tasks.Remove("\\" + name);
    }
    public void RestoreLegacyService(JsonObject service) => Operations.Add("legacy-service");
    public void Enable(string name, bool package = false)
    {
        Registry["HKCU:\\Test|" + name] = new JsonObject
        {
            ["Path"] = "HKCU:\\Test",
            ["Name"] = name,
            ["Existed"] = true,
            ["Type"] = package ? "DWord" : "Binary",
            ["Value"] = package ? JsonValue.Create(2) : JsonValue.Create(Convert.ToBase64String(new byte[] { 2 }))
        };
    }
    public JsonObject Candidate(string name, bool package = false)
    {
        Enable(name, package);
        var candidate = new JsonObject
        {
            ["Id"] = "HKCU:\\Test|" + name,
            ["Name"] = name,
            ["SourceKind"] = package ? "Package" : "Registry",
            ["SourcePath"] = "HKCU:\\Test",
            ["SourceName"] = name,
            ["App"] = package ? new JsonObject { ["Name"] = name, ["Kind"] = "PackageApp", ["AppId"] = "Example!App" } : Fixtures.App(name)
        };
        candidate["App"]!.AsObject().Remove("WaitMode");
        candidate["App"]!.AsObject().Remove("TimeoutSeconds");
        Available.Add(candidate);
        return candidate;
    }
}
static class Fixtures
{
    public static JsonObject App(string name, bool enabled = true) => new()
    {
        ["Name"] = name,
        ["Enabled"] = enabled,
        ["Kind"] = "Command",
        ["FileName"] = "C:\\Example\\" + name + ".exe",
        ["Arguments"] = "--custom",
        ["WaitMode"] = "Launch",
        ["TimeoutSeconds"] = 2,
        ["DelaySeconds"] = 0
    };
    public static JsonObject Group(string name, params JsonObject[] apps) => Configuration.Group(name, "Stop", new JsonArray(apps.Select(a => (JsonNode?)a).ToArray()));
    public static JsonObject Config(params JsonObject[] groups) => new()
    {
        ["SchemaVersion"] = 2,
        ["UserSid"] = "test-user",
        ["TaskName"] = "Test runner",
        ["Groups"] = new JsonArray(groups.Select(g => (JsonNode?)g).ToArray())
    };
}
sealed class StateFixture : IDisposable
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "StartupManagerDotnetTests-" + Guid.NewGuid().ToString("N"));
    public FakeSystem System { get; } = new();
    public StartupService Service
    {
        get;
    }
    public StateFixture()
    {
        Service = new StartupService(DirectoryPath, System);
        Service.Initialize();
        Save(Fixtures.Config(Fixtures.Group("First"), Fixtures.Group("Second")));
        System.RegisterTask("Test runner", "\\", WindowsTasks.CreateXml(System.UserSid, "C:\\Example\\StartupManager.exe", "--run", "C:\\Example", true));
    }
    public void Save(JsonObject config) => Write(Service.FilePath("config.json"), config);
    public void Request(string groupId, params string[] ids) => Write(Service.FilePath("pending-import.json"), new JsonObject
    {
        ["GroupId"] = groupId,
        ["Ids"] = new JsonArray(ids.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray())
    });
    public string GroupId(int index = 0) => Text(Service.Load()["Groups"]![index]!.AsObject(), "Id");
    public void Dispose() => Directory.Delete(DirectoryPath, true);
}
