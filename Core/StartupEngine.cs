using System.Text.Json.Nodes;
using static StartupManager.Core.Configuration;

namespace StartupManager.Core;

public interface IAppLauncher
{
    IDisposable? Launch(JsonObject app);
    bool IsReady(JsonObject app, IDisposable? launch);
}

// A clock makes timeout and stability tests fast without starting real apps.
public interface IStartupClock
{
    DateTimeOffset Now
    {
        get;
    }
    void Sleep(TimeSpan duration);
}
public sealed class StartupClock : IStartupClock
{
    public DateTimeOffset Now => DateTimeOffset.UtcNow;
    public void Sleep(TimeSpan duration) => Thread.Sleep(duration);
}
public sealed class StartupEngine(IAppLauncher launcher, IStartupClock clock, Action<string> log)
{
    sealed class Pending(JsonObject app, IDisposable? launch, DateTimeOffset deadline)
    {
        public JsonObject App { get; } = app;
        public IDisposable? Launch { get; } = launch;
        public DateTimeOffset Deadline { get; } = deadline;
        public DateTimeOffset? ReadySince
        {
            get; set;
        }
    }
    public void Run(JsonObject config)
    {
        Validate(config);
        foreach (var group in Objects(config, "Groups"))
        {
            if (!Enabled(group))
            {
                continue;
            }

            log("Group started: " + Text(group, "Name"));
            var pending = new List<Pending>();
            bool failed = false;
            try
            {
                // Issue every launch request before polling the group's readiness checks.
                foreach (var app in Objects(group, "Apps"))
                {
                    if (!Enabled(app))
                    {
                        continue;
                    }

                    try
                    {
                        var handle = launcher.Launch(app);
                        pending.Add(new Pending(app, handle, clock.Now.AddSeconds(Number(app, "TimeoutSeconds", 60))));
                        log("Launch accepted: " + Text(app, "Name"));
                    }
                    catch (Exception ex) { failed = true; log("Launch failed: " + Text(app, "Name") + " — " + ex.Message); }
                }
                while (pending.Count > 0)
                {
                    foreach (var item in pending.ToArray())
                    {
                        bool finished = false;
                        try
                        {
                            bool ready = launcher.IsReady(item.App, item.Launch);
                            if (ready)
                            {
                                item.ReadySince ??= clock.Now;
                                if ((clock.Now - item.ReadySince.Value).TotalSeconds >= Number(item.App, "StableSeconds") + Number(item.App, "DelaySeconds"))
                                {
                                    log("Ready: " + Text(item.App, "Name"));
                                    finished = true;
                                }
                            }
                            else
                            {
                                item.ReadySince = null;
                            }

                            if (!finished && clock.Now >= item.Deadline)
                            {
                                throw new TimeoutException("Timed out waiting for " + Text(item.App, "Name"));
                            }
                        }
                        catch (Exception ex) { log(ex.Message); failed = true; finished = true; }
                        if (finished)
                        {
                            item.Launch?.Dispose();
                            pending.Remove(item);
                        }
                    }
                    if (pending.Count > 0)
                    {
                        clock.Sleep(TimeSpan.FromMilliseconds(250));
                    }
                }
            }
            finally
            {
                foreach (var item in pending)
                {
                    item.Launch?.Dispose();
                }
            }
            if (failed && Text(group, "OnFailure") != "Continue")
            {
                throw new InvalidOperationException("Group failed: " + Text(group, "Name") + ". Later groups were held.");
            }

            clock.Sleep(TimeSpan.FromSeconds(Number(group, "DelayAfterSeconds")));
            log("Group complete: " + Text(group, "Name"));
        }
    }
}
