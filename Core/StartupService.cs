using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using static StartupManager.Core.Configuration;

namespace StartupManager.Core;

// File transactions and startup policy live here; Windows API details live in WindowsSystem.
public sealed class StartupService(string directory, IStartupSystem system)
{
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public string FilePath(string name) => Path.Combine(DirectoryPath, name);
    public void Initialize()
    {
        Directory.CreateDirectory(DirectoryPath);
        if (File.Exists(FilePath("config.json")))
        {
            var existing = Read(FilePath("config.json"));
            CheckAccount(existing);
            if (Number(existing, "SchemaVersion") == 2)
            {
                Validate(existing);
                return;
            }
            if (Number(existing, "SchemaVersion") is not (0 or 1))
            {
                throw new InvalidDataException("Unsupported configuration version.");
            }

            var migrated = Migrate(existing);
            Validate(migrated);
            if (!File.Exists(FilePath("config.v1.json")))
            {
                File.Copy(FilePath("config.json"), FilePath("config.v1.json"), false);
            }
            else if (!JsonNode.DeepEquals(existing, Read(FilePath("config.v1.json"))))
            {
                throw new IOException("An earlier migration backup already exists. It was preserved; inspect it before retrying.");
            }

            Write(FilePath("config.json"), migrated);
            return;
        }
        if (File.Exists(FilePath("backup.json")))
        {
            var backup = ReadBackup(FilePath("backup.json"));
            if (Objects(backup, "Registry").Any() || Objects(backup, "Tasks").Any() || backup["Service"] != null)
            {
                throw new IOException("Configuration is missing but a recovery backup exists. Restore the configuration before creating a new sequence.");
            }
        }
        else
        {
            Write(FilePath("backup.json"), EmptyBackup());
        }

        Write(FilePath("config.json"), new JsonObject
        {
            ["SchemaVersion"] = 2,
            ["UserSid"] = system.UserSid,
            ["TaskName"] = "Startup Manager - " + system.UserSid,
            ["Groups"] = new JsonArray()
        });
    }
    public JsonObject Load()
    {
        var config = Read(FilePath("config.json"));
        CheckAccount(config);
        Validate(config);
        return config;
    }
    void CheckAccount(JsonObject config)
    {
        if (Text(config, "UserSid") != system.UserSid)
        {
            throw new InvalidOperationException("Use the account that created this configuration.");
        }
    }
    void RequireAdministrator()
    {
        if (!system.IsAdministrator)
        {
            throw new UnauthorizedAccessException("Run this change as administrator under the account that created the configuration.");
        }
    }
    public List<string> Conflicts(JsonObject backup)
    {
        var result = new List<string>();
        foreach (var entry in Objects(backup, "Registry"))
        {
            if (system.RegistryConflict(entry))
            {
                result.Add(Text(entry, "Name"));
            }
        }

        foreach (var entry in Objects(backup, "Tasks"))
        {
            var task = system.FindTask(Text(entry, "Name"), Text(entry, "Path"));
            if (task != null && WindowsTasks.HasEnabledLogon(task.Xml))
            {
                result.Add(Text(entry, "Name"));
            }
        }
        return result;
    }
    public JsonObject State()
    {
        var config = Load();
        var backup = ReadBackup(FilePath("backup.json"));
        var task = system.FindTask(Text(config, "TaskName"));
        bool needsRepair = false;
        if (task != null)
        {
            var taskXml = XDocument.Parse(task.Xml);
            XNamespace ns = taskXml.Root!.Name.Namespace;
            var action = taskXml.Root.Element(ns + "Actions")?.Element(ns + "Exec");
            needsRepair = !string.Equals(Path.GetFileName(action?.Element(ns + "Command")?.Value ?? ""), "StartupManager.exe", StringComparison.OrdinalIgnoreCase) ||
                !(action?.Element(ns + "Arguments")?.Value.StartsWith("--run ", StringComparison.Ordinal) ?? false);
        }
        return new JsonObject
        {
            ["Task"] = task?.State switch { null => "Not installed", 1 => "Disabled", 2 => "Queued", 3 => "Ready", 4 => "Running", _ => "Unknown" },
            ["LastResult"] = task == null ? null : JsonValue.Create(task.LastResult),
            ["Groups"] = Objects(config, "Groups").Count(),
            ["Apps"] = Objects(config, "Groups").Sum(g => Objects(g, "Apps").Count()),
            ["NeedsRepair"] = needsRepair,
            ["Conflicts"] = new JsonArray(Conflicts(backup).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray())
        };
    }
    public JsonArray Candidates() => new(system.Candidates().Select(c => (JsonNode?)c).ToArray());
    JsonObject Capture(JsonObject candidate)
    {
        if (Text(candidate, "SourceKind") is "Registry" or "Package")
        {
            return system.CaptureRegistry(Text(candidate, "SourcePath"), Text(candidate, "SourceName"),
                Text(candidate, "SourceKind") == "Package" ? "DisablePackage" : "DisableRun");
        }

        var task = system.FindTask(Text(candidate, "TaskName"), Text(candidate, "TaskPath")) ?? throw new IOException("Startup task no longer exists.");
        return new JsonObject { ["Name"] = task.Name, ["Path"] = task.Path, ["Xml"] = task.Xml };
    }
    static void AddBackup(JsonObject backup, JsonObject record, bool registry)
    {
        string section = registry ? "Registry" : "Tasks";
        if (!Objects(backup, section).Any(e => Equal(Text(e, "Name"), Text(record, "Name")) && Equal(Text(e, "Path"), Text(record, "Path"))))
        {
            backup[section]!.AsArray().Add(record.DeepClone());
        }
    }
    static bool Equal(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    void Disable(JsonObject changed)
    {
        foreach (var entry in Objects(changed, "Registry"))
        {
            if (Text(entry, "Change") is "DisableRun" or "DisablePackage")
            {
                system.DisableRegistry(entry);
            }
        }

        foreach (var entry in Objects(changed, "Tasks"))
        {
            // Read the current definition, so repair retains an app's upgraded task action.
            var current = system.FindTask(Text(entry, "Name"), Text(entry, "Path")) ?? throw new IOException("Startup task no longer exists.");
            system.RegisterTask(current.Name, current.Path, WindowsTasks.DisableLogon(current.Xml));
        }
        if (Conflicts(changed).Count > 0)
        {
            throw new IOException("Independent startup entries remain enabled.");
        }
    }
    // Keep trying after a restore failure, and report every item requiring attention.
    void RestoreEntries(JsonObject backup)
    {
        var errors = new List<string>();
        foreach (var entry in Objects(backup, "Registry"))
        {
            try
            {
                system.RestoreRegistry(entry);
            }
            catch (Exception ex) { errors.Add(Text(entry, "Name") + ": " + ex.Message); }
        }

        foreach (var entry in Objects(backup, "Tasks"))
        {
            try
            {
                system.RegisterTask(Text(entry, "Name"), Text(entry, "Path"), Text(entry, "Xml"));
            }
            catch (Exception ex) { errors.Add(Text(entry, "Name") + ": " + ex.Message); }
        }

        if (backup["Service"] is JsonObject service)
        {
            try
            {
                system.RestoreLegacyService(service);
            }
            catch (Exception ex) { errors.Add("Legacy service: " + ex.Message); }
        }

        if (errors.Count > 0)
        {
            throw new IOException("Restore failed: " + string.Join("; ", errors));
        }
    }
    public void Restore()
    {
        RequireAdministrator();
        var config = Load();
        var backup = ReadBackup(FilePath("backup.json"));
        // Do not remove the runner if originals could not be restored.
        RestoreEntries(backup);
        system.DeleteTask(Text(config, "TaskName"));
    }
    public void Install(string executable)
    {
        RequireAdministrator();
        var config = Load();
        var backup = ReadBackup(FilePath("backup.json"));
        var before = EmptyBackup();
        var existingTask = system.FindTask(Text(config, "TaskName"));
        bool taskChanged = false;
        try
        {
            var apps = Objects(config, "Groups").SelectMany(g => Objects(g, "Apps")).ToArray();
            foreach (var candidate in system.Candidates(true))
            {
                var app = candidate["App"]!.AsObject();
                bool owned = apps.Any(a => Equal(Text(a, "SourceId"), Text(candidate, "Id")) ||
                    (Text(a, "Kind") == "Command" && Text(app, "Kind") == "Command" && Equal(Text(a, "FileName"), Text(app, "FileName"))) ||
                    (Text(a, "Kind") == "PackageApp" && Text(app, "Kind") == "PackageApp" && Equal(Text(a, "AppId"), Text(app, "AppId"))) ||
                    (Text(a, "Kind") == "Task" && Text(app, "Kind") == "Task" && Equal(Text(a, "TaskName"), Text(app, "TaskName")) && Equal(Text(a, "TaskPath"), Text(app, "TaskPath"))));
                if (owned)
                {
                    var record = Capture(candidate);
                    AddBackup(backup, record, record["Xml"] == null);
                }
            }
            // Persist original backups before touching Windows.
            Write(FilePath("backup.json"), backup);
            foreach (var entry in Objects(backup, "Registry"))
            {
                before["Registry"]!.AsArray().Add(system.CaptureRegistry(Text(entry, "Path"), Text(entry, "Name"), Text(entry, "Change")));
            }

            foreach (var entry in Objects(backup, "Tasks"))
            {
                var current = system.FindTask(Text(entry, "Name"), Text(entry, "Path")) ?? throw new IOException("Startup task no longer exists.");
                before["Tasks"]!.AsArray().Add(new JsonObject { ["Name"] = current.Name, ["Path"] = current.Path, ["Xml"] = current.Xml });
            }
            string arguments = "--run --state-directory " + Quote(DirectoryPath);
            system.RegisterTask(Text(config, "TaskName"), "\\", WindowsTasks.CreateXml(system.UserSid, executable, arguments, Path.GetDirectoryName(executable)!, true));
            taskChanged = true;
            Disable(backup);
            Write(FilePath("install-status.json"), new JsonObject
            {
                ["Success"] = true,
                ["Time"] = DateTimeOffset.Now.ToString("o"),
                ["Task"] = Text(config, "TaskName"),
                ["VerifiedFlags"] = Objects(backup, "Registry").Count()
            });
        }
        catch (Exception ex)
        {
            string failure = ex.Message;
            try
            {
                RestoreEntries(before);
                if (taskChanged)
                {
                    if (existingTask == null)
                    {
                        system.DeleteTask(Text(config, "TaskName"));
                    }
                    else
                    {
                        system.RegisterTask(existingTask.Name, existingTask.Path, existingTask.Xml);
                    }
                }
            }
            catch (Exception rollback) { failure += "; rollback: " + rollback.Message; }
            Write(FilePath("install-status.json"), new JsonObject { ["Success"] = false, ["Error"] = failure });
            throw new IOException(failure, ex);
        }
    }
    public int Import()
    {
        string beforeConfig = File.ReadAllText(FilePath("config.json")), beforeBackup = File.ReadAllText(FilePath("backup.json"));
        var changed = EmptyBackup();
        bool settingsWritten = false, windowsTouched = false;
        try
        {
            RequireAdministrator();
            var config = Load();
            var backup = ReadBackup(FilePath("backup.json"));
            if (system.FindTask(Text(config, "TaskName")) == null)
            {
                throw new InvalidOperationException("Install the ordered startup sequence before importing apps.");
            }

            var request = Read(FilePath("pending-import.json"));
            var group = Objects(config, "Groups").SingleOrDefault(g => Equal(Text(g, "Id"), Text(request, "GroupId"))) ?? throw new InvalidOperationException("Choose an existing startup group.");
            var requested = request["Ids"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
            var ids = requested.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var selected = system.Candidates().Where(c => ids.Contains(Text(c, "Id"))).ToArray();
            if (ids.Count == 0 || ids.Count != requested.Length || selected.Length != ids.Count)
            {
                throw new IOException("Startup settings changed since the list was opened. Refresh the list and try again.");
            }

            foreach (var candidate in selected)
            {
                var record = Capture(candidate);
                bool registry = record["Xml"] == null;
                AddBackup(changed, record, registry);
                AddBackup(backup, record, registry);
                var app = candidate["App"]!.DeepClone().AsObject();
                app["SourceId"] = Text(candidate, "Id");
                app["Enabled"] = true;
                foreach (var sourceGroup in Objects(config, "Groups"))
                {
                    var apps = sourceGroup["Apps"]!.AsArray();
                    foreach (var old in apps.OfType<JsonObject>().Where(a => Equal(Text(a, "SourceId"), Text(candidate, "Id"))).ToArray())
                    {
                        foreach (string field in new[] { "WaitMode", "ProcessName", "ServiceName", "PortFile", "TimeoutSeconds", "StableSeconds", "DelaySeconds" })
                        {
                            if (old[field] != null)
                            {
                                app[field] = old[field]!.DeepClone();
                            }
                        }

                        apps.Remove(old);
                    }
                }
                if (app["WaitMode"] == null)
                {
                    string mode = "Launch", processName = "";
                    if (Text(app, "Kind") == "Command" && Path.GetExtension(Text(app, "FileName")).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        mode = "Process";
                        processName = Path.GetFileNameWithoutExtension(Text(app, "FileName"));
                        var match = Regex.Match(Text(app, "Arguments"), "--processStart\\s+\"?([^\"\\s]+\\.exe)", RegexOptions.IgnoreCase);
                        if (match.Success)
                        {
                            processName = Path.GetFileNameWithoutExtension(match.Groups[1].Value);
                        }
                        else if (Text(app, "Arguments").Contains("--checkInstall", StringComparison.OrdinalIgnoreCase))
                        {
                            mode = "Launch";
                            processName = "";
                        }
                    }
                    app["WaitMode"] = mode;
                    if (processName != "")
                    {
                        app["ProcessName"] = processName;
                    }

                    app["DelaySeconds"] = 2;
                }
                group["Apps"]!.AsArray().Add(app);
            }
            Validate(config);
            settingsWritten = true;
            Write(FilePath("backup.json"), backup);
            windowsTouched = true;
            Disable(changed);
            Write(FilePath("config.json"), config);
            Write(FilePath("import-result.json"), new JsonObject { ["Success"] = true, ["Count"] = selected.Length });
            return selected.Length;
        }
        catch (Exception ex)
        {
            string failure = ex.Message;
            bool restored = true;
            if (windowsTouched)
            {
                try
                {
                    RestoreEntries(changed);
                }
                catch (Exception rollback) { restored = false; failure += "; rollback: " + rollback.Message; }
            }

            if (settingsWritten)
            {
                File.WriteAllText(FilePath("config.json"), beforeConfig);
                // Preserve recovery records if Windows rollback failed.
                if (restored)
                {
                    File.WriteAllText(FilePath("backup.json"), beforeBackup);
                }
            }
            Write(FilePath("import-result.json"), new JsonObject { ["Success"] = false, ["Error"] = failure });
            throw new IOException(failure, ex);
        }
    }
    public void Run(IAppLauncher launcher, IStartupClock? clock = null)
    {
        var config = Load();
        using var mutex = new Mutex(false, "Local\\StartupManager-" + system.UserSid);
        bool locked;
        try
        {
            locked = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException) { locked = true; }
        if (!locked)
        {
            return;
        }

        void Log(string message) => File.AppendAllText(FilePath("startup.log"), DateTimeOffset.Now.ToString("o") + " " + message + Environment.NewLine);
        try
        {
            Log("Sequence started.");
            new StartupEngine(launcher, clock ?? new StartupClock(), Log).Run(config);
            Log("Sequence complete.");
        }
        catch (Exception ex) { Log("ERROR: " + ex.Message); throw; }
        finally { mutex.ReleaseMutex(); }
    }
    public static string Quote(string argument)
    {
        // State directories are absolute Windows paths: quotes are invalid in a path.
        if (argument.Contains('"'))
        {
            throw new ArgumentException("A path cannot contain quotes.");
        }

        return "\"" + Regex.Replace(argument, @"(\\+)$", "$1$1") + "\"";
    }
}
