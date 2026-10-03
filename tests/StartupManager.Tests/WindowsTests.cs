using Microsoft.Win32;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using StartupManager.Core;

namespace StartupManager.Tests;

public class WindowsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public void ExitReadinessRequiresSuccessfulHelperExit(int exitCode)
    {
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c exit " + exitCode)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        Assert.True(process.WaitForExit(5000));
        var app = new JsonObject { ["Name"] = "Isolated exit check", ["WaitMode"] = "Exit" };
        var system = new WindowsSystem();
        if (exitCode == 0)
        {
            Assert.True(system.IsReady(app, process));
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => system.IsReady(app, process));
        }
    }
    [Fact]
    public void NativeRegistryRoundTripPreservesSiblingValuesAndAbsentEntries()
    {
        string path = "Software\\StartupManagerDotnetTests\\" + Guid.NewGuid().ToString("N");
        string fullPath = "HKCU:\\" + path;
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        try
        {
            using var key = root.CreateSubKey(path);
            byte[] enabled = new byte[12];
            enabled[0] = 2;
            key.SetValue("Example", enabled, RegistryValueKind.Binary);
            key.SetValue("Sibling", "untouched");
            var system = new WindowsSystem();
            var original = system.CaptureRegistry(fullPath, "Example", "DisableRun");
            system.DisableRegistry(original);
            Assert.False(system.RegistryConflict(original));
            Assert.Equal("untouched", key.GetValue("Sibling"));
            system.RestoreRegistry(original);
            Assert.Equal(enabled, Assert.IsType<byte[]>(key.GetValue("Example")));
            var missing = system.CaptureRegistry(fullPath, "Missing", "DisableRun");
            system.DisableRegistry(missing);
            system.RestoreRegistry(missing);
            Assert.Null(key.GetValue("Missing"));
            key.SetValue("State", 2);
            var package = system.CaptureRegistry(fullPath, "State", "DisablePackage");
            system.DisableRegistry(package);
            Assert.Equal(1, key.GetValue("State"));
            system.RestoreRegistry(package);
            Assert.Equal(2, key.GetValue("State"));
        }
        finally { root.DeleteSubKeyTree(path, false); }
    }
    [Fact]
    public void TaskRepairChangesOnlyLogonTriggers()
    {
        string original = "<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><Triggers><LogonTrigger/><TimeTrigger><Enabled>true</Enabled></TimeTrigger></Triggers><Actions><Exec><Command>C:\\Updated\\App.exe</Command></Exec></Actions></Task>";
        string patched = WindowsTasks.DisableLogon(original);
        Assert.False(WindowsTasks.HasEnabledLogon(patched));
        var xml = XDocument.Parse(patched);
        XNamespace ns = xml.Root!.Name.Namespace;
        Assert.Equal("C:\\Updated\\App.exe", xml.Descendants(ns + "Command").Single().Value);
        Assert.Equal("true", xml.Descendants(ns + "TimeTrigger").Single().Element(ns + "Enabled")!.Value);
    }
    [Theory]
    [InlineData("\"C:\\Program Files\\Example.exe\" --flag", "C:\\Program Files\\Example.exe", "--flag")]
    [InlineData("C:\\Program Files\\Example.EXE --flag", "C:\\Program Files\\Example.EXE", "--flag")]
    public void RegistryCommandParsingPreservesArguments(string command, string file, string arguments)
    {
        var result = WindowsSystem.ParseCommand(command)!;
        Assert.Equal(file, Configuration.Text(result, "FileName"));
        Assert.Equal(arguments, Configuration.Text(result, "Arguments"));
    }
    [Fact]
    public void PortFileRequiresCurrentProcessAndFreshListeningPort()
    {
        using var process = Process.GetCurrentProcess();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        string file = Path.Combine(Path.GetTempPath(), "StartupManagerPort-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            File.WriteAllText(file, "{\"port\":" + port + "}");
            var app = new JsonObject { ["Name"] = "Test", ["WaitMode"] = "PortFile", ["ProcessName"] = process.ProcessName, ["PortFile"] = file };
            var system = new WindowsSystem();
            Assert.True(system.IsReady(app, null));
            File.SetLastWriteTimeUtc(file, process.StartTime.ToUniversalTime().AddMinutes(-1));
            Assert.False(system.IsReady(app, null));
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow);
            listener.Stop();
            Assert.False(system.IsReady(app, null));
        }
        finally { listener.Stop(); File.Delete(file); }
    }
    [Fact]
    public void TaskSchedulerCanBeReadWithoutChangingUserTasks()
    {
        using var tasks = new WindowsTasks();
        Assert.Null(tasks.Find("Startup Manager test missing " + Guid.NewGuid().ToString("N")));
        Assert.NotNull(tasks.All());
    }
    [Fact]
    public void NativeTaskRegistrationAndDeletionRoundTrip()
    {
        using var tasks = new WindowsTasks();
        var system = new WindowsSystem();
        string name = "Startup Manager dotnet test " + Guid.NewGuid().ToString("N");
        try
        {
            string xml = WindowsTasks.CreateXml(system.UserSid, Environment.ProcessPath!, "--test-unused", AppContext.BaseDirectory, false);
            tasks.Register(name, "\\", xml);
            var actual = tasks.Find(name)!;
            Assert.NotNull(actual);
            Assert.Contains("--test-unused", actual.Xml);
            Assert.False(WindowsTasks.HasEnabledLogon(actual.Xml));
        }
        finally { tasks.Delete(name); }
        Assert.Null(tasks.Find(name));
    }
}
