using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using static StartupManager.Core.Configuration;

namespace StartupManager.Core;

public static class AppPaths
{
    public static string StateDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StartupManager", "State");
    public static string Executable => Environment.ProcessPath ?? throw new InvalidOperationException("Cannot find the executable path.");
    public static string DesktopTask => "Startup Manager desktop launch - " + new WindowsSystem().UserSid;
    public static void CopyLegacyState(string source, string destination, string sid)
    {
        source = Path.GetFullPath(source);
        destination = Path.GetFullPath(destination);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var config = Read(Path.Combine(source, "config.json"));
        if (Text(config, "UserSid") != sid)
        {
            throw new InvalidOperationException("Legacy configuration belongs to another account.");
        }

        if (File.Exists(Path.Combine(destination, "config.json")))
        {
            throw new IOException("Destination already has a configuration. Existing settings were not overwritten.");
        }
        if (!File.Exists(Path.Combine(source, "backup.json")))
        {
            throw new IOException("Legacy restore backup is missing.");
        }
        Validate(Number(config, "SchemaVersion") == 2 ? config : Migrate(config));
        // Copy the recovery backup first. An interrupted migration is safe to retry.
        Directory.CreateDirectory(destination);
        foreach (string name in new[] { "backup.json", "config.v1.json", "startup.log", "install-status.json" })
        {
            if (File.Exists(Path.Combine(source, name)))
            {
                File.Copy(Path.Combine(source, name), Path.Combine(destination, name), true);
            }
        }

        File.Copy(Path.Combine(source, "config.json"), Path.Combine(destination, "config.json"));
    }
}

// The GUI and scheduled runner share one executable; CLI modes never open a window.
public static class BackendCommands
{
    public static bool IsCommand(string[] args) => args.Length > 0 && args[0] is "--initialize" or "--state" or "--candidates" or
        "--install" or "--import" or "--restore" or "--run" or "--uninstall" or "--migrate-from";
    public static int Execute(string[] args)
    {
        // WinExe modes have no attached console. Write UTF-8 to inherited pipes
        // without calling SetConsoleOutputCP (which fails without a console).
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });
        string directory = AppPaths.StateDirectory;
        int directoryIndex = Array.IndexOf(args, "--state-directory");
        if (directoryIndex >= 0)
        {
            if (directoryIndex + 1 >= args.Length)
            {
                Console.Error.WriteLine("Missing state directory.");
                return 1;
            }
            directory = Path.GetFullPath(args[directoryIndex + 1]);
        }
        var system = new WindowsSystem();
        var service = new StartupService(directory, system);
        try
        {
            switch (args[0])
            {
                case "--initialize":
                    string legacyDirectory = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.FullName;
                    if (string.Equals(directory, AppPaths.StateDirectory, StringComparison.OrdinalIgnoreCase) &&
                        !File.Exists(service.FilePath("config.json")) && File.Exists(Path.Combine(legacyDirectory, "config.json")))
                    {
                        AppPaths.CopyLegacyState(legacyDirectory, directory, system.UserSid);
                    }

                    service.Initialize();
                    break;
                case "--state":
                    Console.WriteLine(service.State().ToJsonString());
                    break;
                case "--candidates":
                    Console.WriteLine(service.Candidates().ToJsonString());
                    break;
                case "--install":
                    service.Install(AppPaths.Executable);
                    break;
                case "--import":
                    service.Import();
                    break;
                case "--restore":
                    service.Restore();
                    break;
                case "--run":
                    service.Run(system);
                    break;
                case "--migrate-from":
                    if (args.Length < 2 || args[1].StartsWith("--"))
                    {
                        throw new ArgumentException("Supply the old folder containing config.json and backup.json.");
                    }

                    AppPaths.CopyLegacyState(args[1], directory, system.UserSid);
                    service.Initialize();
                    break;
                case "--uninstall":
                    if (!File.Exists(service.FilePath("config.json")))
                    {
                        if (File.Exists(service.FilePath("backup.json")))
                        {
                            var orphaned = ReadBackup(service.FilePath("backup.json"));
                            if (Objects(orphaned, "Registry").Any() || Objects(orphaned, "Tasks").Any() || orphaned["Service"] != null)
                            {
                                throw new IOException("Configuration is missing but a recovery backup exists. Restore the configuration before uninstalling.");
                            }
                        }
                        break;
                    }

                    var uninstallConfig = service.Load();
                    var uninstallBackup = ReadBackup(service.FilePath("backup.json"));
                    if (system.FindTask(Text(uninstallConfig, "TaskName")) == null &&
                        !Objects(uninstallBackup, "Registry").Any() && !Objects(uninstallBackup, "Tasks").Any() && uninstallBackup["Service"] == null)
                    {
                        break;
                    }

                    if (!system.IsAdministrator)
                    {
                        var info = new ProcessStartInfo(AppPaths.Executable) { UseShellExecute = true, Verb = "runas" };
                        foreach (string arg in new[] { "--uninstall", "--state-directory", directory })
                        {
                            info.ArgumentList.Add(arg);
                        }

                        using var elevated = Process.Start(info) ?? throw new IOException("Could not restore startup settings before uninstalling.");
                        elevated.WaitForExit();
                        return elevated.ExitCode;
                    }
                    service.Restore();
                    break;
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(service.FilePath("backend-error.log"), ex.ToString());
            }
            catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { }
            return 1;
        }
    }
    public static void LaunchDesktop()
    {
        string executable = AppPaths.Executable;
        using var tasks = new WindowsTasks();
        tasks.Register(AppPaths.DesktopTask, "\\", WindowsTasks.CreateXml(new WindowsSystem().UserSid, executable, "--desktop", Path.GetDirectoryName(executable)!, false));
        tasks.Start(AppPaths.DesktopTask);
    }
    public static void CleanupDesktopTask()
    {
        using var tasks = new WindowsTasks();
        tasks.Delete(AppPaths.DesktopTask);
    }
}
