using System.Text.Json.Nodes;
using StartupManager.Core;
using static StartupManager.Tests.Fixtures;

namespace StartupManager.Tests;

public class EngineTests
{
    [Fact]
    public void AllMembersLaunchBeforePollingAndNextGroupWaitsForAll()
    {
        var clock = new FakeClock();
        var launcher = new FakeLauncher(clock);
        var skipped = Group("Skipped", App("Skipped"));
        skipped["Enabled"] = false;
        var config = Config(Group("First", App("Slow"), App("Fast"), App("Disabled", false)), skipped, Group("Second", App("Later")));
        new StartupEngine(launcher, clock, message => launcher.Events.Add(message)).Run(config);
        Assert.True(launcher.Events.IndexOf("start:Fast") < launcher.Events.IndexOf("check:Slow"));
        Assert.True(launcher.Events.IndexOf("start:Later") > launcher.Events.IndexOf("Ready: Slow"));
        Assert.DoesNotContain("start:Disabled", launcher.Events);
        Assert.DoesNotContain("start:Skipped", launcher.Events);
    }
    [Fact]
    public void TimeoutStopsLaterGroups()
    {
        var clock = new FakeClock();
        var launcher = new FakeLauncher(clock);
        Assert.Throws<InvalidOperationException>(() => new StartupEngine(launcher, clock, _ => { }).Run(Config(Group("First", App("Never")), Group("Later", App("Later")))));
        Assert.DoesNotContain("start:Later", launcher.Events);
    }
    [Fact]
    public void ContinueAllowsNextGroupAfterLaunchFailure()
    {
        var clock = new FakeClock();
        var launcher = new FakeLauncher(clock);
        var group = Group("First", App("Broken"));
        group["OnFailure"] = "Continue";
        new StartupEngine(launcher, clock, _ => { }).Run(Config(group, Group("Later", App("Later"))));
        Assert.Contains("start:Later", launcher.Events);
    }
    [Fact]
    public void StabilityResetsWhenReadinessDrops()
    {
        var clock = new FakeClock();
        var launcher = new FakeLauncher(clock);
        var start = clock.Now;
        launcher.Ready = _ => (clock.Now - start).TotalSeconds != 0.5;
        var app = App("Flaky");
        app["StableSeconds"] = 1;
        app["TimeoutSeconds"] = 4;
        new StartupEngine(launcher, clock, _ => { }).Run(Config(Group("First", app)));
        Assert.Equal(1.75, (clock.Now - start).TotalSeconds);
    }
    [Fact]
    public void GroupDelayRunsAfterMembersAreReady()
    {
        var clock = new FakeClock();
        var launcher = new FakeLauncher(clock);
        var start = clock.Now;
        var app = App("First");
        app["DelaySeconds"] = 1;
        var group = Group("First", app);
        group["DelayAfterSeconds"] = 3;
        new StartupEngine(launcher, clock, _ => { }).Run(Config(group));
        Assert.Equal(4, (clock.Now - start).TotalSeconds);
    }
    [Theory]
    [InlineData("WaitMode", "NotAProbe")]
    [InlineData("FileName", "relative.exe")]
    [InlineData("ProcessName", "")]
    public void InvalidConfigurationIsRejectedBeforeLaunch(string field, string value)
    {
        var app = App("Bad");
        app["WaitMode"] = "Process";
        app["ProcessName"] = "Bad";
        app[field] = value;
        var clock = new FakeClock();
        var launcher = new FakeLauncher(clock);
        Assert.Throws<InvalidDataException>(() => new StartupEngine(launcher, clock, _ => { }).Run(Config(Group("Bad", app))));
        Assert.Empty(launcher.Events);
    }
    [Fact]
    public void DuplicateSourcesAndImpossibleTimingAreRejected()
    {
        var first = App("First");
        first["SourceId"] = "same";
        var second = App("Second");
        second["SourceId"] = "SAME";
        Assert.Throws<InvalidDataException>(() => Configuration.Validate(Config(Group("Duplicate", first, second))));
        first["DelaySeconds"] = 2;
        Assert.Throws<InvalidDataException>(() => Configuration.Validate(Config(Group("Timing", first.DeepClone().AsObject()))));
    }
}
