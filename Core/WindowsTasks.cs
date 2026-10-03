using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace StartupManager.Core;

public sealed record ScheduledEntry(string Name, string Path, string Xml, int State, int LastResult);

// Task Scheduler exposes its supported Windows API through COM. No shell commands.
public sealed class WindowsTasks : IDisposable
{
    readonly dynamic service;
    public WindowsTasks()
    {
        service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        service.Connect();
    }
    public ScheduledEntry? Find(string name, string path = "\\")
    {
        dynamic folder;
        try
        {
            folder = service.GetFolder(path);
        }
        catch (Exception ex) when ((uint)ex.HResult is 0x80070002 or 0x8004130F) { return null; }
        try
        {
            dynamic task;
            try
            {
                task = folder.GetTask(name);
            }
            catch (Exception ex) when ((uint)ex.HResult is 0x80070002 or 0x8004130F) { return null; }
            try
            {
                return Read(task);
            }
            finally { Marshal.ReleaseComObject(task); }
        }
        finally { Marshal.ReleaseComObject(folder); }
    }
    static ScheduledEntry Read(dynamic task)
    {
        string fullPath = task.Path;
        int separator = fullPath.LastIndexOf('\\');
        return new ScheduledEntry((string)task.Name, fullPath[..(separator + 1)], (string)task.Xml, (int)task.State, (int)task.LastTaskResult);
    }
    public List<ScheduledEntry> All()
    {
        var result = new List<ScheduledEntry>();
        void Visit(dynamic folder)
        {
            try
            {
                dynamic tasks = folder.GetTasks(1);
                try
                {
                    for (int i = 1; i <= tasks.Count; i++)
                    {
                        dynamic task = tasks[i];
                        try
                        {
                            result.Add(Read(task));
                        }
                        finally { Marshal.ReleaseComObject(task); }
                    }
                }
                finally { Marshal.ReleaseComObject(tasks); }
                dynamic folders = folder.GetFolders(0);
                try
                {
                    for (int i = 1; i <= folders.Count; i++)
                    {
                        Visit(folders[i]);
                    }
                }
                finally { Marshal.ReleaseComObject(folders); }
            }
            catch (Exception ex) when ((uint)ex.HResult == 0x80070005) { /* Inaccessible system folders are excluded. */ }
            finally { Marshal.ReleaseComObject(folder); }
        }
        Visit(service.GetFolder("\\"));
        return result;
    }
    public void Register(string name, string path, string xml)
    {
        dynamic folder = service.GetFolder(path);
        try
        {
            // TASK_CREATE_OR_UPDATE; preserve the principal in the XML.
            dynamic task = folder.RegisterTask(name, xml, 6, null, null, 0, null);
            Marshal.ReleaseComObject(task);
        }
        finally { Marshal.ReleaseComObject(folder); }
    }
    public void Delete(string name)
    {
        if (Find(name) == null)
        {
            return;
        }

        dynamic folder = service.GetFolder("\\");
        try
        {
            folder.DeleteTask(name, 0);
        }
        finally { Marshal.ReleaseComObject(folder); }
    }
    public void Start(string name, string path = "\\")
    {
        dynamic folder = service.GetFolder(path);
        try
        {
            dynamic task = folder.GetTask(name);
            try
            {
                dynamic running = task.Run(null);
                Marshal.ReleaseComObject(running);
            }
            finally { Marshal.ReleaseComObject(task); }
        }
        finally { Marshal.ReleaseComObject(folder); }
    }
    public static bool HasEnabledLogon(string xml)
    {
        var document = XDocument.Parse(xml);
        XNamespace ns = document.Root!.Name.Namespace;
        return document.Root.Element(ns + "Triggers")?.Elements(ns + "LogonTrigger")
            .Any(trigger => !string.Equals(trigger.Element(ns + "Enabled")?.Value, "false", StringComparison.OrdinalIgnoreCase)) ?? false;
    }
    public static string DisableLogon(string xml)
    {
        var document = XDocument.Parse(xml);
        XNamespace ns = document.Root!.Name.Namespace;
        foreach (var trigger in document.Root.Element(ns + "Triggers")?.Elements(ns + "LogonTrigger") ?? [])
        {
            trigger.SetElementValue(ns + "Enabled", "false");
        }

        return document.ToString();
    }
    public static string CreateXml(string sid, string executable, string arguments, string workingDirectory, bool atLogon)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        XElement Element(string name, object content) => new(ns + name, content);
        return new XDocument(new XElement(ns + "Task", new XAttribute("version", "1.2"),
            Element("RegistrationInfo", Element("Description", "Launch user-defined startup groups in order.")),
            Element("Triggers", atLogon ? Element("LogonTrigger", new[] { Element("Enabled", "true"), Element("UserId", sid) }) : Array.Empty<XElement>()),
            Element("Principals", new XElement(ns + "Principal", new XAttribute("id", "User"), Element("UserId", sid),
                Element("LogonType", "InteractiveToken"), Element("RunLevel", "LeastPrivilege"))),
            Element("Settings", new[] { Element("MultipleInstancesPolicy", "IgnoreNew"), Element("DisallowStartIfOnBatteries", "false"),
                Element("StopIfGoingOnBatteries", "false"), Element("StartWhenAvailable", "true"), Element("ExecutionTimeLimit", "PT24H") }),
            new XElement(ns + "Actions", new XAttribute("Context", "User"), Element("Exec", new[] { Element("Command", executable),
                Element("Arguments", arguments), Element("WorkingDirectory", workingDirectory) })))).ToString();
    }
    public void Dispose() => Marshal.ReleaseComObject(service);
}
