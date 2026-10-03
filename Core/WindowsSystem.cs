using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using static StartupManager.Core.Configuration;

namespace StartupManager.Core;

public interface IStartupSystem
{
    string UserSid
    {
        get;
    }
    bool IsAdministrator
    {
        get;
    }
    List<JsonObject> Candidates(bool includeDisabled = false);
    JsonObject CaptureRegistry(string path, string name, string change);
    void DisableRegistry(JsonObject record);
    void RestoreRegistry(JsonObject record);
    bool RegistryConflict(JsonObject record);
    ScheduledEntry? FindTask(string name, string path = "\\");
    void RegisterTask(string name, string path, string xml);
    void DeleteTask(string name);
    void RestoreLegacyService(JsonObject service);
}

public sealed class WindowsSystem : IStartupSystem, IAppLauncher
{
    public string UserSid => WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("No Windows user SID.");
    public bool IsAdministrator => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    public ScheduledEntry? FindTask(string name, string path = "\\")
    {
        using var tasks = new WindowsTasks();
        return tasks.Find(name, path);
    }
    public void RegisterTask(string name, string path, string xml)
    {
        using var tasks = new WindowsTasks();
        tasks.Register(name, path, xml);
    }
    public void DeleteTask(string name)
    {
        using var tasks = new WindowsTasks();
        tasks.Delete(name);
    }

    static RegistryKey? Open(string path, bool writable = false, bool create = false)
    {
        string[] parts = path.Split(':', 2);
        using var root = RegistryKey.OpenBaseKey(parts[0] switch
        {
            "HKCU" => RegistryHive.CurrentUser,
            "HKLM" => RegistryHive.LocalMachine,
            _ => throw new InvalidDataException("Unsupported registry hive.")
        }, RegistryView.Registry64);
        string subkey = parts[1].TrimStart('\\');
        return create ? root.CreateSubKey(subkey, true) : root.OpenSubKey(subkey, writable);
    }
    static int Approved(string path, string name)
    {
        using var key = Open(path);
        return key?.GetValue(name) is byte[] { Length: > 0 } bytes ? bytes[0] : 2;
    }
    public JsonObject CaptureRegistry(string path, string name, string change)
    {
        using var key = Open(path);
        bool existed = key?.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase) ?? false;
        RegistryValueKind type = existed ? key!.GetValueKind(name) : change == "DisablePackage" ? RegistryValueKind.DWord : RegistryValueKind.Binary;
        JsonNode? value = null;
        if (existed)
        {
            object raw = key!.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)!;
            value = type switch
            {
                RegistryValueKind.Binary => JsonValue.Create(Convert.ToBase64String((byte[])raw)),
                RegistryValueKind.DWord => JsonValue.Create((int)raw),
                RegistryValueKind.QWord => JsonValue.Create((long)raw),
                RegistryValueKind.String or RegistryValueKind.ExpandString => JsonValue.Create((string)raw),
                RegistryValueKind.MultiString => new JsonArray(((string[])raw).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
                _ => throw new InvalidDataException("Unsupported registry backup type: " + type)
            };
        }
        return new JsonObject { ["Path"] = path, ["Name"] = name, ["Existed"] = existed, ["Type"] = type.ToString(), ["Value"] = value, ["Change"] = change };
    }
    public void DisableRegistry(JsonObject record)
    {
        using var key = Open(Text(record, "Path"), true, true)!;
        if (Text(record, "Change") == "DisableRun")
        {
            byte[] disabled = new byte[12];
            disabled[0] = 3;
            key.SetValue(Text(record, "Name"), disabled, RegistryValueKind.Binary);
        }
        else if (Text(record, "Change") == "DisablePackage")
        {
            key.SetValue(Text(record, "Name"), 1, RegistryValueKind.DWord);
        }
        else
        {
            throw new InvalidDataException("Unknown startup registry change.");
        }

        if (RegistryConflict(record))
        {
            throw new IOException("Could not disable startup entry: " + Text(record, "Name"));
        }
    }
    public void RestoreRegistry(JsonObject record)
    {
        if (!(record["Existed"]?.GetValue<bool>() ?? false))
        {
            using var absentKey = Open(Text(record, "Path"), true);
            absentKey?.DeleteValue(Text(record, "Name"), false);
            return;
        }
        using var key = Open(Text(record, "Path"), true, true)!;
        var type = Enum.Parse<RegistryValueKind>(Text(record, "Type"), true);
        var value = record["Value"]!;
        object raw = type switch
        {
            RegistryValueKind.Binary => Convert.FromBase64String(value.GetValue<string>()),
            RegistryValueKind.DWord => value.GetValue<int>(),
            RegistryValueKind.QWord => value.GetValue<long>(),
            RegistryValueKind.String or RegistryValueKind.ExpandString => value.GetValue<string>(),
            RegistryValueKind.MultiString => value.AsArray().Select(s => s!.GetValue<string>()).ToArray(),
            _ => throw new InvalidDataException("Unsupported registry backup type.")
        };
        key.SetValue(Text(record, "Name"), raw, type);
    }
    public bool RegistryConflict(JsonObject record)
    {
        if (Text(record, "Change") == "DisableRun")
        {
            return Approved(Text(record, "Path"), Text(record, "Name")) is 2 or 6;
        }

        if (Text(record, "Change") == "DisablePackage")
        {
            using var key = Open(Text(record, "Path"));
            return key?.GetValue(Text(record, "Name")) is int state && state == 2;
        }
        return false;
    }
    public static JsonObject? ParseCommand(string command)
    {
        string expanded = Environment.ExpandEnvironmentVariables(command);
        var match = Regex.Match(expanded, "^\"([^\"]+)\"\\s*(.*)$");
        if (!match.Success)
        {
            match = Regex.Match(expanded, @"^(.+?\.exe)\s*(.*)$", RegexOptions.IgnoreCase);
        }

        if (!match.Success)
        {
            return null;
        }

        return new JsonObject
        {
            ["FileName"] = match.Groups[1].Value,
            ["Arguments"] = match.Groups[2].Value,
            ["WorkingDirectory"] = Path.GetDirectoryName(match.Groups[1].Value)
        };
    }
    static JsonObject Candidate(string path, string name, string origin, string sourceKind, JsonObject app) => new()
    {
        ["Id"] = path + "|" + name,
        ["Name"] = Text(app, "Name"),
        ["Origin"] = origin,
        ["SourceKind"] = sourceKind,
        ["SourcePath"] = path,
        ["SourceName"] = name,
        ["App"] = app
    };
    public List<JsonObject> Candidates(bool includeDisabled = false)
    {
        var result = new List<JsonObject>();
        const string current = @"\Software\Microsoft\Windows\CurrentVersion\";
        foreach (var (hive, wow, label) in new[] { ("HKCU:", false, "User startup"), ("HKLM:", false, "Machine startup"),
            ("HKLM:", true, "Machine startup (32-bit)"), ("HKCU:", true, "User startup (32-bit)") })
        {
            string run = hive + (wow ? @"\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\" : current) + "Run";
            string approved = hive + current + @"Explorer\StartupApproved\" + (wow ? "Run32" : "Run");
            using var key = Open(run);
            foreach (string name in key?.GetValueNames() ?? [])
            {
                if (name is "SecurityHealth" or "RtkAudUService" or "Elgato Ordered Startup")
                {
                    continue;
                }

                if (!includeDisabled && Approved(approved, name) is not (2 or 6))
                {
                    continue;
                }

                string command = key!.GetValue(name)?.ToString() ?? "";
                var app = ParseCommand(command);
                if (app == null)
                {
                    continue;
                }

                app["Name"] = name;
                app["Kind"] = "Command";
                var candidate = Candidate(approved, name, label, "Registry", app);
                candidate["RunPath"] = run;
                candidate["Command"] = command;
                result.Add(candidate);
            }
        }
        foreach (var folderKind in new[] { Environment.SpecialFolder.Startup, Environment.SpecialFolder.CommonStartup })
        {
            string folder = Environment.GetFolderPath(folderKind);
            if (!Directory.Exists(folder))
            {
                continue;
            }

            string approved = (folderKind == Environment.SpecialFolder.Startup ? "HKCU:" : "HKLM:") + current + @"Explorer\StartupApproved\StartupFolder";
            foreach (string file in Directory.EnumerateFiles(folder))
            {
                if (!new[] { ".lnk", ".exe", ".cmd", ".bat" }.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                string name = Path.GetFileName(file);
                if (!includeDisabled && Approved(approved, name) is not (2 or 6))
                {
                    continue;
                }

                result.Add(Candidate(approved, name, "Startup folder", "Registry", new JsonObject
                {
                    ["Name"] = Path.GetFileNameWithoutExtension(file),
                    ["Kind"] = "Command",
                    ["FileName"] = file,
                    ["Arguments"] = "",
                    ["WorkingDirectory"] = folder
                }));
            }
        }
        AddPackages(result, includeDisabled);
        using var tasks = new WindowsTasks();
        foreach (var task in tasks.All())
        {
            if (task.Path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase) || task.Name == "Elgato Ordered Startup" ||
                task.Name.StartsWith("Startup Manager", StringComparison.OrdinalIgnoreCase) || task.State == 1 || !WindowsTasks.HasEnabledLogon(task.Xml) ||
                Regex.IsMatch(task.Name, "Update|Telemetry|Metrics|Security|Defender", RegexOptions.IgnoreCase))
            {
                continue;
            }

            var xml = XDocument.Parse(task.Xml);
            XNamespace ns = xml.Root!.Name.Namespace;
            if (!(xml.Root.Element(ns + "Actions")?.Elements(ns + "Exec").Any() ?? false))
            {
                continue;
            }

            var app = new JsonObject { ["Name"] = task.Path + task.Name, ["Kind"] = "Task", ["TaskName"] = task.Name, ["TaskPath"] = task.Path };
            result.Add(new JsonObject
            {
                ["Id"] = "Task|" + task.Path + task.Name,
                ["Name"] = task.Path + task.Name,
                ["Origin"] = "Logon task",
                ["SourceKind"] = "Task",
                ["TaskName"] = task.Name,
                ["TaskPath"] = task.Path,
                ["App"] = app
            });
        }
        return result;
    }
    static void AddPackages(List<JsonObject> result, bool includeDisabled)
    {
        const string basePath = @"HKCU:\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";
        using var data = Open(basePath);
        foreach (string family in data?.GetSubKeyNames() ?? [])
        {
            foreach (string package in NativeMethods.PackageNames(family))
            {
                string? location = NativeMethods.PackagePath(package);
                if (location == null)
                {
                    continue;
                }

                XDocument manifest;
                try
                {
                    manifest = XDocument.Load(Path.Combine(location, "AppxManifest.xml"));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException) { continue; }
                foreach (var application in manifest.Descendants().Where(e => e.Name.LocalName == "Application"))
                {
                    foreach (var extension in application.Descendants().Where(e => e.Name.LocalName == "Extension" && (string?)e.Attribute("Category") == "windows.startupTask"))
                    {
                        var startup = extension.Elements().FirstOrDefault(e => e.Name.LocalName == "StartupTask");
                        string? taskId = (string?)startup?.Attribute("TaskId");
                        if (taskId == null)
                        {
                            continue;
                        }

                        string path = basePath + "\\" + family + "\\" + taskId;
                        using var key = Open(path);
                        if (key == null || (!includeDisabled && key.GetValue("State") is not 2))
                        {
                            continue;
                        }

                        string name = ((string?)manifest.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Identity")?.Attribute("Name") ?? family) + " / " + taskId;
                        var candidate = Candidate(path, "State", "Packaged startup app", "Package", new JsonObject
                        {
                            ["Name"] = name,
                            ["Kind"] = "PackageApp",
                            ["AppId"] = family + "!" + (string?)application.Attribute("Id")
                        });
                        if (!result.Any(c => Text(c, "Id") == Text(candidate, "Id")))
                        {
                            result.Add(candidate);
                        }
                    }
                }
            }
        }
    }
    static List<Process> SessionProcesses(string name)
    {
        int session = Process.GetCurrentProcess().SessionId;
        var result = new List<Process>();
        foreach (var process in Process.GetProcessesByName(name))
        {
            try
            {
                if (process.SessionId == session)
                {
                    result.Add(process);
                    continue;
                }
            }
            catch (InvalidOperationException) { }
            process.Dispose();
        }
        return result;
    }
    public IDisposable? Launch(JsonObject app)
    {
        string service = Text(app, "ServiceName");
        var deadline = DateTime.UtcNow.AddSeconds(Number(app, "TimeoutSeconds", 60));
        while (service != "" && !NativeMethods.ServiceRunning(service))
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Required service is not running: " + service);
            }

            Thread.Sleep(250);
        }
        if (Text(app, "WaitMode") is "Process" or "Responsive" or "PortFile" && Text(app, "ProcessName") != "")
        {
            var processes = SessionProcesses(Text(app, "ProcessName"));
            bool running = processes.Count > 0;
            foreach (var process in processes)
            {
                process.Dispose();
            }

            if (running)
            {
                return null;
            }
        }
        switch (Text(app, "Kind"))
        {
            case "Command":
                return Process.Start(new ProcessStartInfo(Text(app, "FileName"), Text(app, "Arguments"))
                {
                    UseShellExecute = true,
                    WorkingDirectory = Text(app, "WorkingDirectory")
                });
            case "PackageApp":
                using (Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                    "shell:AppsFolder\\" + Text(app, "AppId"))
                {
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                }))
                {
                }
                return null;
            case "Task":
                using (var tasks = new WindowsTasks())
                {
                    tasks.Start(Text(app, "TaskName"), Text(app, "TaskPath"));
                }

                return null;
            default:
                throw new InvalidDataException("Unknown app kind.");
        }
    }
    public bool IsReady(JsonObject app, IDisposable? launch)
    {
        if (Text(app, "ServiceName") != "" && !NativeMethods.ServiceRunning(Text(app, "ServiceName")))
        {
            return false;
        }

        string mode = Text(app, "WaitMode");
        if (mode is "" or "Launch")
        {
            return true;
        }

        if (mode == "Exit")
        {
            if (launch is not Process process)
            {
                throw new InvalidOperationException("No process handle for exit check: " + Text(app, "Name"));
            }

            process.Refresh();
            if (!process.HasExited)
            {
                return false;
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException("App exited with code " + process.ExitCode + ": " + Text(app, "Name"));
            }

            return true;
        }
        var processes = SessionProcesses(Text(app, "ProcessName"));
        try
        {
            if (processes.Count == 0)
            {
                return false;
            }

            if (mode == "Process")
            {
                return true;
            }

            if (mode == "Responsive")
            {
                return processes.Any(p => p.Responding);
            }

            if (mode == "PortFile")
            {
                try
                {
                    string file = Environment.ExpandEnvironmentVariables(Text(app, "PortFile"));
                    if (File.GetLastWriteTimeUtc(file) < processes.Min(p => p.StartTime.ToUniversalTime()).AddSeconds(-2))
                    {
                        return false;
                    }

                    int port = Read(file)["port"]!.GetValue<int>();
                    return port is >= 1 and <= 65535 && NativeMethods.PortListening(port, processes.Select(p => p.Id).ToHashSet());
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or
                    InvalidOperationException or System.ComponentModel.Win32Exception or FormatException)
                {
                    return false;
                }
            }
            throw new InvalidDataException("Unknown wait mode: " + mode);
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }
    public void RestoreLegacyService(JsonObject service)
    {
        const string path = @"HKLM:\SYSTEM\CurrentControlSet\Services\WavelinkSEService";
        if (service["FailureActionsExisted"]?.GetValue<bool>() ?? false)
        {
            RestoreRegistry(new JsonObject { ["Path"] = path, ["Name"] = "FailureActions", ["Existed"] = true, ["Type"] = "Binary", ["Value"] = service["FailureActions"]!.DeepClone() });
        }
        else
        {
            NativeMethods.ClearServiceFailureActions("WavelinkSEService");
            using var key = Open(path, true);
            key?.DeleteValue("FailureActions", false);
        }
        if (service["Start"] != null)
        {
            RestoreRegistry(new JsonObject { ["Path"] = path, ["Name"] = "Start", ["Existed"] = true, ["Type"] = "DWord", ["Value"] = service["Start"]!.DeepClone() });
        }
    }
}
