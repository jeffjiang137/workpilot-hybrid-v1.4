namespace WorkPilot.Host.Core.Health;

/// <summary>Health of the background Host as observed by the scheduler-side probe.</summary>
public enum HostHealthStatus
{
    /// <summary>No heartbeat has ever been observed (Host never ran, or probe unavailable).</summary>
    Unknown,
    /// <summary>A recent heartbeat exists; the Host is considered alive.</summary>
    Healthy,
    /// <summary>The last heartbeat is older than the freshness threshold; the Host may have crashed.</summary>
    Degraded,
}

/// <summary>
/// A point-in-time health reading of the background Host. Pure data so the state-machine logic in
/// <see cref="HostHealthMonitor"/> is unit-testable without OS access.
/// </summary>
public sealed record HostHealth(HostHealthStatus Status, System.DateTimeOffset? LastHeartbeatUtc, string? Details)
{
    public static HostHealth Unknown(string? details = null) => new(HostHealthStatus.Unknown, null, details);
    public static HostHealth Healthy(System.DateTimeOffset at, string? details = null) => new(HostHealthStatus.Healthy, at, details);
    public static HostHealth Degraded(System.DateTimeOffset at, string? details = null) => new(HostHealthStatus.Degraded, at, details);

    /// <summary>True when the Host is considered alive and crash-recoverable (T08 "Host crash 可恢复").</summary>
    public bool IsAlive => Status == HostHealthStatus.Healthy;
}
