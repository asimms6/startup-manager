using System.Text.Json;
using System.Text.Json.Nodes;

namespace StartupManager.Core;

// Keep the existing JSON format, including fields from older releases.
public static class Configuration
{
    public static string Text(JsonObject value, string key) => value[key]?.GetValue<string>() ?? "";
    public static double Number(JsonObject value, string key, double fallback = 0)
    {
        if (value[key] == null)
        {
            return fallback;
        }

        if (value[key] is JsonValue number)
        {
            if (number.TryGetValue<double>(out double real))
            {
                return real;
            }

            if (number.TryGetValue<int>(out int integer))
            {
                return integer;
            }

            if (number.TryGetValue<long>(out long large))
            {
                return large;
            }

            if (number.TryGetValue<decimal>(out decimal precise))
            {
                return (double)precise;
            }
        }
        throw new InvalidDataException("Expected a number: " + key);
    }
    public static bool Enabled(JsonObject value) => value["Enabled"]?.GetValue<bool>() ?? true;
    public static IEnumerable<JsonObject> Objects(JsonObject value, string key) =>
        (value[key] as JsonArray ?? throw new InvalidDataException($"Missing {key} array.")).Select(node =>
            node as JsonObject ?? throw new InvalidDataException($"Invalid item in {key}."));
    public static JsonObject Read(string path) => JsonNode.Parse(File.ReadAllText(path)) as JsonObject
        ?? throw new InvalidDataException("Expected a JSON object: " + path);
    public static JsonObject ReadBackup(string path)
    {
        var backup = Read(path);
        // Early releases had registry/service backups without a Tasks section.
        backup["Registry"] ??= new JsonArray();
        backup["Tasks"] ??= new JsonArray();
        return backup;
    }
    public static void Write(string path, JsonObject value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, value.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }
    public static JsonObject EmptyBackup() => new() { ["Registry"] = new JsonArray(), ["Tasks"] = new JsonArray() };
    public static void Validate(JsonObject config)
    {
        if (Number(config, "SchemaVersion") != 2 || string.IsNullOrWhiteSpace(Text(config, "UserSid")) ||
            string.IsNullOrWhiteSpace(Text(config, "TaskName")))
        {
            throw new InvalidDataException("Invalid startup group configuration.");
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in Objects(config, "Groups"))
        {
            if (string.IsNullOrWhiteSpace(Text(group, "Id")) || !ids.Add(Text(group, "Id")) || string.IsNullOrWhiteSpace(Text(group, "Name")))
            {
                throw new InvalidDataException("Group names and unique IDs are required.");
            }

            if (Text(group, "OnFailure") is not ("Stop" or "Continue"))
            {
                throw new InvalidDataException("Group failure handling must be Stop or Continue.");
            }

            CheckTiming(group, "DelayAfterSeconds", 0);
            foreach (var app in Objects(group, "Apps"))
            {
                string kind = Text(app, "Kind"), mode = Text(app, "WaitMode");
                if (string.IsNullOrWhiteSpace(Text(app, "Name")) || kind is not ("Command" or "PackageApp" or "Task"))
                {
                    throw new InvalidDataException("Invalid app name or launch type.");
                }

                if (kind == "Command" && !Path.IsPathFullyQualified(Text(app, "FileName")))
                {
                    throw new InvalidDataException("App paths must be absolute.");
                }

                if (kind == "PackageApp" && Text(app, "AppId") == "")
                {
                    throw new InvalidDataException("Packaged apps need an application ID.");
                }

                if (kind == "Task" && (Text(app, "TaskName") == "" || Text(app, "TaskPath") == ""))
                {
                    throw new InvalidDataException("Scheduled apps need a task name and path.");
                }

                if (mode is not ("" or "Launch" or "Process" or "Responsive" or "Exit" or "PortFile"))
                {
                    throw new InvalidDataException("Unknown app readiness check.");
                }

                if (mode is "Process" or "Responsive" or "PortFile" && Text(app, "ProcessName") == "")
                {
                    throw new InvalidDataException("Process-based checks require a process name.");
                }

                if (mode == "PortFile" && Text(app, "PortFile") == "")
                {
                    throw new InvalidDataException("A local server check requires a JSON port file.");
                }

                if (mode == "Exit" && (kind != "Command" || !Text(app, "FileName").EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidDataException("Exit checks require an executable.");
                }

                CheckTiming(app, "TimeoutSeconds", 1);
                CheckTiming(app, "StableSeconds", 0);
                CheckTiming(app, "DelaySeconds", 0);
                double timeout = Number(app, "TimeoutSeconds", 60);
                if (timeout <= Number(app, "StableSeconds") + Number(app, "DelaySeconds"))
                {
                    throw new InvalidDataException("Timeout must exceed the stable time plus extra delay.");
                }

                string source = Text(app, "SourceId");
                if (source != "" && !sources.Add(source))
                {
                    throw new InvalidDataException("A startup entry cannot belong to multiple groups.");
                }
            }
        }
    }
    static void CheckTiming(JsonObject obj, string key, double minimum)
    {
        if (obj[key] == null)
        {
            return;
        }

        double number = Number(obj, key);
        if (!double.IsFinite(number) || number < minimum || number > 3600)
        {
            throw new InvalidDataException("Invalid timing setting: " + key);
        }
    }
    public static JsonObject Migrate(JsonObject old)
    {
        string family = Text(old, "WaveFamily");
        if (family == "")
        {
            family = "Elgato.WaveLink_g54w8ztgkx496";
        }

        string appId = Text(old, "WaveAppId");
        if (appId == "")
        {
            appId = family + "!App";
        }

        var stream = new JsonObject
        {
            ["Name"] = "Stream Deck",
            ["Kind"] = "Command",
            ["FileName"] = Text(old, "StreamDeckPath"),
            ["Arguments"] = "--runinbk",
            ["WorkingDirectory"] = Path.GetDirectoryName(Text(old, "StreamDeckPath")),
            ["Enabled"] = true,
            ["WaitMode"] = "Responsive",
            ["ProcessName"] = "StreamDeck",
            ["TimeoutSeconds"] = 60,
            ["StableSeconds"] = 5,
            ["DelaySeconds"] = 3
        };
        var wave = new JsonObject
        {
            ["Name"] = "Wave Link",
            ["Kind"] = "PackageApp",
            ["AppId"] = appId,
            ["Enabled"] = true,
            ["WaitMode"] = "PortFile",
            ["ProcessName"] = "Elgato.WaveLink",
            ["ServiceName"] = "WavelinkSEService",
            ["PortFile"] = "%LOCALAPPDATA%\\Packages\\" + family + "\\LocalState\\ws-info.json",
            ["TimeoutSeconds"] = 90,
            ["StableSeconds"] = 6,
            ["DelaySeconds"] = 3
        };
        return new JsonObject
        {
            ["SchemaVersion"] = 2,
            ["UserSid"] = Text(old, "UserSid"),
            ["TaskName"] = "Elgato Ordered Startup",
            ["Groups"] = new JsonArray(Group("Stream Deck", "Stop", new JsonArray(stream)), Group("Wave Link", "Stop", new JsonArray(wave)),
                Group("Other apps", "Continue", old["Apps"]?.DeepClone() as JsonArray ?? new JsonArray()))
        };
    }
    public static JsonObject Group(string name, string failure, JsonArray apps) => new()
    {
        ["Id"] = Guid.NewGuid().ToString("N"),
        ["Name"] = name,
        ["Enabled"] = true,
        ["OnFailure"] = failure,
        ["DelayAfterSeconds"] = 0,
        ["Apps"] = apps
    };
}
