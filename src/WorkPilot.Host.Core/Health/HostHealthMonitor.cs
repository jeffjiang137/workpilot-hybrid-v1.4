using WorkPilot.Contracts.Primitives;

namespace WorkPilot.Host.Core.Health;

/// <summary>
/// Pure evaluator of background-Host health from the last observed heartbeat. Used by the Host
/// bootstrap to decide whether the scheduler-side task is alive and by monitoring to detect a
/// crashed Host (T08 "Host crash 可恢复"). No OS access — fully unit-testable.
/// </summary>
public static class HostHealthMonitor
{
    /// <summary>
    /// Evaluate health from the last heartbeat.
    /// </summary>
    /// <param name="lastHeartbeatUtc">Last heartbeat timestamp, or null if never observed.</param>
    /// <param name="now">Current time (injected clock).</param>
    /// <param name="freshThreshold">Max age for a heartbeat still considered healthy.</param>
    public static HostHealth Evaluate(System.DateTimeOffset? lastHeartbeatUtc, DateTimeOffset now, System.TimeSpan freshThreshold)
    {
        if (lastHeartbeatUtc is null)
            return HostHealth.Unknown("no heartbeat observed");

        var age = now - lastHeartbeatUtc.Value;
        if (age <= freshThreshold)
            return HostHealth.Healthy(lastHeartbeatUtc.Value);

        return HostHealth.Degraded(lastHeartbeatUtc.Value,
            $"heartbeat age {age.TotalSeconds:0}s exceeds threshold {freshThreshold.TotalSeconds:0}s");
    }
}
