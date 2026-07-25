using System;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Host.Core.Health;
using Xunit;

namespace WorkPilot.Host.Core.Tests;

public class HostHealthMonitorTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Threshold = TimeSpan.FromMinutes(2);

    [Fact]
    public void Null_heartbeat_is_unknown()
        => Assert.Equal(HostHealthStatus.Unknown, HostHealthMonitor.Evaluate(null, Now, Threshold).Status);

    [Fact]
    public void Fresh_heartbeat_is_healthy()
    {
        var health = HostHealthMonitor.Evaluate(Now.AddSeconds(10), Now, Threshold);
        Assert.Equal(HostHealthStatus.Healthy, health.Status);
        Assert.True(health.IsAlive);
    }

    [Fact]
    public void Stale_heartbeat_is_degraded_and_not_alive()
    {
        var health = HostHealthMonitor.Evaluate(Now.AddMinutes(-10), Now, Threshold);
        Assert.Equal(HostHealthStatus.Degraded, health.Status);
        Assert.False(health.IsAlive);
    }
}
