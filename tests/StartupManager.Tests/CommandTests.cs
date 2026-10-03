using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace StartupManager.Tests;

public class CommandTests
{
    static string Executable()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "StartupManager.csproj")))
        {
            directory = directory.Parent;
        }

        if (directory == null)
        {
            throw new IOException("Cannot find the app project.");
        }
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        return Path.Combine(directory.FullName, "bin", configuration, "net8.0-windows", "StartupManager.exe");
    }
    static (int Code, string Output, string Error) Run(string directory, string command)
    {
        var info = new ProcessStartInfo(Executable())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in new[] { command, "--state-directory", directory })
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000))
        {
            process.Kill(true);
            throw new TimeoutException("Headless mode did not exit.");
        }
        return (process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
    }
    [Fact]
    public void GuiExecutableSupportsRedirectedHeadlessModes()
    {
        string directory = Path.Combine(Path.GetTempPath(), "StartupManagerCommands-" + Guid.NewGuid().ToString("N"));
        try
        {
            var initialize = Run(directory, "--initialize");
            Assert.Equal(0, initialize.Code);
            Assert.Empty(initialize.Error);
            var config = StartupManager.Core.Configuration.Read(Path.Combine(directory, "config.json"));
            config["TaskName"] = "Startup Manager command test " + Guid.NewGuid().ToString("N");
            StartupManager.Core.Configuration.Write(Path.Combine(directory, "config.json"), config);
            var state = Run(directory, "--state");
            Assert.Equal(0, state.Code);
            var json = JsonNode.Parse(state.Output)!;
            Assert.Equal("Not installed", json["Task"]!.GetValue<string>());
            Assert.Equal(0, json["Groups"]!.GetValue<int>());
            var candidates = Run(directory, "--candidates");
            Assert.Equal(0, candidates.Code);
            Assert.IsType<JsonArray>(JsonNode.Parse(candidates.Output));
            var run = Run(directory, "--run");
            Assert.Equal(0, run.Code);
            Assert.Contains("Sequence complete", File.ReadAllText(Path.Combine(directory, "startup.log")));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }
    [Fact]
    public void HeadlessFailureReturnsNonzeroAndWritesError()
    {
        string directory = Path.Combine(Path.GetTempPath(), "StartupManagerCommands-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = Run(directory, "--state");
            Assert.Equal(1, result.Code);
            Assert.NotEmpty(result.Error);
            Assert.True(File.Exists(Path.Combine(directory, "backend-error.log")));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
