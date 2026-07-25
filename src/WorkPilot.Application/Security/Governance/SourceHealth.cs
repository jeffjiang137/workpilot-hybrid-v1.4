using System;

namespace WorkPilot.Application.Security.Governance;

/// <summary>Unified source health status (doc 06 §7). <c>Quarantined</c> can only be entered by a
/// Critical detector or manual disposition — a plain enable command cannot recover from it.</summary>
public enum SourceHealthStatus
{
    Unknown = 0,
    Healthy = 1,
    Degraded = 2,
    Expired = 3,
    SchemaStale = 4,
    Disabled = 5,
    Quarantined = 6
}

/// <summary>A single connector/MCP source's observed health (doc 06 §7).</summary>
public sealed record SourceHealth(
    string Kind,
    string Id,
    SourceHealthStatus Status,
    string? Detail,
    DateTimeOffset ObservedAtUtc);
