using System.Text.Json.Nodes;
using StartupManager.Core;
using static StartupManager.Core.Configuration;
using static StartupManager.Tests.Fixtures;

namespace StartupManager.Tests;

public class ServiceTests
{
    [Fact]
    public void StateFlagsLegacyRunnerForRepair()
    {
        using var fixture = new StateFixture();
        fixture.System.RegisterTask("Test runner", "\\", WindowsTasks.CreateXml(fixture.System.UserSid, "C:\\Windows\\powershell.exe", "old-script.ps1", "C:\\Example", true));
        Assert.True(fixture.Service.State()["NeedsRepair"]!.GetValue<bool>());
        fixture.Service.Install("C:\\Example\\StartupManager.exe");
        Assert.False(fixture.Service.State()["NeedsRepair"]!.GetValue<bool>());
    }
    [Fact]
    public void CopyingLegacyStatePreservesBackupAndRefusesToOverwrite()
    {
        using var source = new StateFixture();
        using var target = new StateFixture();
        File.Delete(target.Service.FilePath("config.json"));
        string before = File.ReadAllText(source.Service.FilePath("backup.json"));
        AppPaths.CopyLegacyState(source.DirectoryPath, target.DirectoryPath, source.System.UserSid);
        Assert.Equal(before, File.ReadAllText(target.Service.FilePath("backup.json")));
        Assert.Equal(File.ReadAllText(source.Service.FilePath("config.json")), File.ReadAllText(target.Service.FilePath("config.json")));
        Assert.Throws<IOException>(() => AppPaths.CopyLegacyState(source.DirectoryPath, target.DirectoryPath, source.System.UserSid));
    }
    [Fact]
    public void LegacyBackupCanOmitTasksAndRestoreService()
    {
        using var fixture = new StateFixture();
        Write(fixture.Service.FilePath("backup.json"), new JsonObject { ["Registry"] = new JsonArray(), ["Service"] = new JsonObject() });
        fixture.Service.Restore();
        Assert.Contains("legacy-service", fixture.System.Operations);
    }
    [Fact]
    public void MissingConfigNeverOverwritesRecoveryBackup()
    {
        using var fixture = new StateFixture();
        var backup = EmptyBackup();
        backup["Registry"]!.AsArray().Add(new JsonObject { ["Name"] = "Recover me" });
        Write(fixture.Service.FilePath("backup.json"), backup);
        string original = File.ReadAllText(fixture.Service.FilePath("backup.json"));
        File.Delete(fixture.Service.FilePath("config.json"));
        Assert.Throws<IOException>(() => fixture.Service.Initialize());
        Assert.Equal(original, File.ReadAllText(fixture.Service.FilePath("backup.json")));
    }
    [Fact]
    public void FirstRunIsEmptyAndReopeningPreservesFiles()
    {
        using var fixture = new StateFixture();
        File.Delete(fixture.Service.FilePath("config.json"));
        fixture.Service.Initialize();
        var config = fixture.Service.Load();
        Assert.Empty(Objects(config, "Groups"));
        Assert.StartsWith("Startup Manager - ", Text(config, "TaskName"));
        string before = File.ReadAllText(fixture.Service.FilePath("config.json"));
        fixture.Service.Initialize();
        Assert.Equal(before, File.ReadAllText(fixture.Service.FilePath("config.json")));
    }
    [Fact]
    public void MigrationPreservesOriginalBackupAndDisabledApps()
    {
        using var fixture = new StateFixture();
        var legacy = new JsonObject { ["UserSid"] = fixture.System.UserSid, ["StreamDeckPath"] = "C:\\Example\\StreamDeck.exe", ["Apps"] = new JsonArray(App("Custom", false)) };
        fixture.Save(legacy);
        string original = File.ReadAllText(fixture.Service.FilePath("config.json"));
        string backup = File.ReadAllText(fixture.Service.FilePath("backup.json"));
        fixture.Service.Initialize();
        var migrated = fixture.Service.Load();
        var groups = Objects(migrated, "Groups").ToArray();
        Assert.Equal(3, groups.Length);
        Assert.False(Enabled(Objects(groups[2], "Apps").Single()));
        Assert.Equal("--custom", Text(Objects(groups[2], "Apps").Single(), "Arguments"));
        Assert.Equal("Responsive", Text(Objects(groups[0], "Apps").Single(), "WaitMode"));
        Assert.Equal("PortFile", Text(Objects(groups[1], "Apps").Single(), "WaitMode"));
        Assert.Equal(original, File.ReadAllText(fixture.Service.FilePath("config.v1.json")));
        Assert.Equal(backup, File.ReadAllText(fixture.Service.FilePath("backup.json")));
    }
    [Fact]
    public void ForeignAccountAndFutureSchemaAreRejected()
    {
        using var fixture = new StateFixture();
        var config = fixture.Service.Load();
        config["UserSid"] = "other";
        fixture.Save(config);
        Assert.Throws<InvalidOperationException>(() => fixture.Service.Initialize());
        config["UserSid"] = fixture.System.UserSid;
        config["SchemaVersion"] = 99;
        fixture.Save(config);
        Assert.Throws<InvalidDataException>(() => fixture.Service.Initialize());
    }
    [Fact]
    public void ImportPreservesSiblingsAndDisabledAppsAndDetectsConflicts()
    {
        using var fixture = new StateFixture();
        var config = fixture.Service.Load();
        config["Groups"]![0]!["Apps"]!.AsArray().Add(App("Already off", false));
        fixture.Save(config);
        var candidate = fixture.System.Candidate("Example");
        fixture.System.Enable("Sibling");
        fixture.Request(fixture.GroupId(), Text(candidate, "Id"));
        Assert.Equal(1, fixture.Service.Import());
        Assert.False(fixture.System.RegistryConflict(new JsonObject { ["Path"] = "HKCU:\\Test", ["Name"] = "Example", ["Change"] = "DisableRun" }));
        Assert.True(fixture.System.RegistryConflict(new JsonObject { ["Path"] = "HKCU:\\Test", ["Name"] = "Sibling", ["Change"] = "DisableRun" }));
        var apps = Objects(fixture.Service.Load()["Groups"]![0]!.AsObject(), "Apps").ToArray();
        Assert.Equal(2, apps.Length);
        Assert.False(Enabled(apps[0]));
        var backup = Read(fixture.Service.FilePath("backup.json"));
        Assert.Empty(fixture.Service.Conflicts(backup));
        fixture.System.Enable("Example");
        Assert.Contains("Example", fixture.Service.Conflicts(backup));
    }
    [Fact]
    public void ReimportMovesAppAndRetainsReadinessAndOriginalBackup()
    {
        using var fixture = new StateFixture();
        var candidate = fixture.System.Candidate("Example");
        fixture.Request(fixture.GroupId(), Text(candidate, "Id"));
        fixture.Service.Import();
        var config = fixture.Service.Load();
        var app = Objects(config["Groups"]![0]!.AsObject(), "Apps").Single();
        app["WaitMode"] = "Responsive";
        app["ProcessName"] = "example";
        fixture.Save(config);
        string backup = File.ReadAllText(fixture.Service.FilePath("backup.json"));
        fixture.System.Enable("Example");
        fixture.Request(fixture.GroupId(1), Text(candidate, "Id"));
        fixture.Service.Import();
        config = fixture.Service.Load();
        Assert.Empty(Objects(config["Groups"]![0]!.AsObject(), "Apps"));
        Assert.Equal("Responsive", Text(Objects(config["Groups"]![1]!.AsObject(), "Apps").Single(), "WaitMode"));
        Assert.Equal(backup, File.ReadAllText(fixture.Service.FilePath("backup.json")));
    }
    [Fact]
    public void StaleImportDoesNotChangeConfigurationOrBackup()
    {
        using var fixture = new StateFixture();
        fixture.System.Candidate("Example");
        string config = File.ReadAllText(fixture.Service.FilePath("config.json")), backup = File.ReadAllText(fixture.Service.FilePath("backup.json"));
        fixture.Request(fixture.GroupId(), "missing");
        Assert.Throws<IOException>(() => fixture.Service.Import());
        Assert.Equal(config, File.ReadAllText(fixture.Service.FilePath("config.json")));
        Assert.Equal(backup, File.ReadAllText(fixture.Service.FilePath("backup.json")));
        Assert.False(Read(fixture.Service.FilePath("import-result.json"))["Success"]!.GetValue<bool>());
    }
    [Fact]
    public void FailedImportRollsBackTouchedEntriesAndFiles()
    {
        using var fixture = new StateFixture();
        var a = fixture.System.Candidate("First");
        var b = fixture.System.Candidate("Second");
        fixture.System.FailDisable = "Second";
        fixture.Request(fixture.GroupId(), Text(a, "Id"), Text(b, "Id"));
        string config = File.ReadAllText(fixture.Service.FilePath("config.json")), backup = File.ReadAllText(fixture.Service.FilePath("backup.json"));
        Assert.Throws<IOException>(() => fixture.Service.Import());
        Assert.Equal(config, File.ReadAllText(fixture.Service.FilePath("config.json")));
        Assert.Equal(backup, File.ReadAllText(fixture.Service.FilePath("backup.json")));
        Assert.Contains("restore:First", fixture.System.Operations);
        Assert.Contains("restore:Second", fixture.System.Operations);
    }
    [Fact]
    public void FailedRollbackRetainsRecoveryBackup()
    {
        using var fixture = new StateFixture();
        var a = fixture.System.Candidate("First");
        var b = fixture.System.Candidate("Second");
        fixture.System.FailDisable = "Second";
        fixture.System.FailRestore = "First";
        fixture.Request(fixture.GroupId(), Text(a, "Id"), Text(b, "Id"));
        Assert.Throws<IOException>(() => fixture.Service.Import());
        Assert.Equal(2, Objects(Read(fixture.Service.FilePath("backup.json")), "Registry").Count());
        Assert.Contains("rollback", Text(Read(fixture.Service.FilePath("import-result.json")), "Error"));
    }
    [Fact]
    public void PackageStateIsDisabledAndRestored()
    {
        using var fixture = new StateFixture();
        var candidate = fixture.System.Candidate("Package", true);
        fixture.Request(fixture.GroupId(), Text(candidate, "Id"));
        fixture.Service.Import();
        Assert.Equal(1, Number(fixture.System.Registry["HKCU:\\Test|Package"], "Value"));
        fixture.Service.Restore();
        Assert.Equal(2, Number(fixture.System.Registry["HKCU:\\Test|Package"], "Value"));
        Assert.Null(fixture.System.FindTask("Test runner"));
    }
    [Fact]
    public void InstallUsesDotnetRunnerAndRollbackPreservesPreviousTask()
    {
        using var fixture = new StateFixture();
        var candidate = fixture.System.Candidate("Example");
        var config = fixture.Service.Load();
        config["Groups"]![0]!["Apps"]!.AsArray().Add(candidate["App"]!.DeepClone());
        fixture.Save(config);
        fixture.Service.Install("C:\\Example\\StartupManager.exe");
        var installed = fixture.System.FindTask("Test runner")!;
        Assert.Contains("--run --state-directory", installed.Xml);
        Assert.DoesNotContain("powershell", installed.Xml, StringComparison.OrdinalIgnoreCase);
        fixture.System.FailDisable = "Example";
        Assert.Throws<IOException>(() => fixture.Service.Install("C:\\New\\StartupManager.exe"));
        Assert.Equal(installed.Xml, fixture.System.FindTask("Test runner")!.Xml);
    }
    [Fact]
    public void RestoreFailureKeepsRunnerAndTriesRemainingEntries()
    {
        using var fixture = new StateFixture();
        var a = fixture.System.Candidate("First");
        var b = fixture.System.Candidate("Second");
        fixture.Request(fixture.GroupId(), Text(a, "Id"), Text(b, "Id"));
        fixture.Service.Import();
        fixture.System.FailRestore = "First";
        Assert.Throws<IOException>(() => fixture.Service.Restore());
        Assert.NotNull(fixture.System.FindTask("Test runner"));
        Assert.Contains("restore:Second", fixture.System.Operations);
    }
    [Fact]
    public void NonAdministratorCannotModifyStartup()
    {
        using var fixture = new StateFixture();
        fixture.System.IsAdministrator = false;
        Assert.Throws<UnauthorizedAccessException>(() => fixture.Service.Install("C:\\Example\\App.exe"));
        Assert.Throws<UnauthorizedAccessException>(() => fixture.Service.Restore());
    }
}
