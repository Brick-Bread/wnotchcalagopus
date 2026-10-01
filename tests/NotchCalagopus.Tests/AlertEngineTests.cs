using NotchCalagopus.Api;
using NotchCalagopus.Monitoring;

namespace NotchCalagopus.Tests;

public class AlertEngineTests
{
    private static ServerView Server(string state, long memoryBytes = 0, long memoryLimitMib = 1000) =>
        new(
            new ServerInfo { Uuid = "u1", Name = "Survival", Limits = new ServerLimits { Memory = memoryLimitMib, Disk = 1000 } },
            new ResourceUsage { State = state, MemoryBytes = memoryBytes });

    [Fact]
    public void CrashRaisesOfflineAlertUntilAcknowledged()
    {
        var engine = new AlertEngine(new PluginOptions());
        engine.Observe(Server("running"), true);
        engine.Observe(Server("offline"), true);

        Assert.Equal(["Survival"], engine.OfflineAlerts);

        engine.Acknowledge("u1");
        Assert.Empty(engine.OfflineAlerts);
    }

    [Fact]
    public void StoppingFirstIsAPlainStop()
    {
        var engine = new AlertEngine(new PluginOptions());
        engine.Observe(Server("running"), true);
        engine.Observe(Server("stopping"), true);
        IReadOnlyList<ServerEvent> events = engine.Observe(Server("offline"), true);

        Assert.Empty(engine.OfflineAlerts);
        Assert.Equal(ServerEventKind.Stopped, Assert.Single(events).Kind);
    }

    [Fact]
    public void ComingBackAfterACrashIsBackOnline()
    {
        var engine = new AlertEngine(new PluginOptions());
        engine.Observe(Server("running"), true);
        engine.Observe(Server("offline"), true);
        IReadOnlyList<ServerEvent> events = engine.Observe(Server("running"), true);

        Assert.Equal(ServerEventKind.BackOnline, Assert.Single(events).Kind);
        Assert.Empty(engine.OfflineAlerts);
    }

    [Fact]
    public void FirstObservationIsOnlyABaseline()
    {
        var engine = new AlertEngine(new PluginOptions());

        Assert.Empty(engine.Observe(Server("running"), true));
        Assert.Empty(engine.OfflineAlerts);
    }

    [Fact]
    public void ThresholdNeedsSeveralPollsAndLiveUpdatesDoNotCount()
    {
        var engine = new AlertEngine(new PluginOptions { MemoryPercent = 90, ThresholdPolls = 3 });
        ServerView hot = Server("running", memoryBytes: 950L * 1024 * 1024);

        engine.Observe(hot, true);
        engine.Observe(hot, true);
        for (int i = 0; i < 10; i++)
        {
            engine.Observe(hot, countThresholds: false);
        }

        Assert.Empty(engine.Breaches);

        engine.Observe(hot, true);
        Breach breach = Assert.Single(engine.Breaches);
        Assert.Equal(Metric.Memory, breach.Metric);
    }

    [Fact]
    public void DismissedBreachStaysQuietUntilItHasRecovered()
    {
        var engine = new AlertEngine(new PluginOptions { MemoryPercent = 90, ThresholdPolls = 1 });
        ServerView hot = Server("running", memoryBytes: 950L * 1024 * 1024);
        ServerView calm = Server("running", memoryBytes: 100L * 1024 * 1024);

        engine.Observe(hot, true);
        engine.Acknowledge("u1");
        engine.Observe(hot, true);
        Assert.Empty(engine.Breaches);

        engine.Observe(calm, true);
        engine.Observe(hot, true);
        Assert.Single(engine.Breaches);
    }

    [Fact]
    public void ZeroThresholdSwitchesTheCheckOff()
    {
        var engine = new AlertEngine(new PluginOptions { MemoryPercent = 0, ThresholdPolls = 1 });
        engine.Observe(Server("running", memoryBytes: 999L * 1024 * 1024), true);

        Assert.Empty(engine.Breaches);
    }
}
