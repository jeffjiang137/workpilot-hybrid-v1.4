using System.Collections.Generic;
using System.Linq;
using WorkPilot.Contracts.Primitives.Ids;

namespace WorkPilot.Domain.Security;

/// <summary>
/// An aggregated security incident (doc 06 §3). Same-fingerprint events within a sliding window
/// collapse into one Open/Acknowledged incident; severity may only rise; up to 20 distinct recent
/// evidence digests are retained. A fresh same-fingerprint event after <see cref="IncidentState.Resolved"/>
/// re-opens the incident rather than creating a duplicate storm.
/// </summary>
public sealed record Incident(
    IncidentId Id,
    string Fingerprint,
    IncidentState State,
    SecuritySeverity Severity,
    SecurityEventType Type,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc,
    int Count,
    IReadOnlyList<string> RecentEvidenceDigests,
    string? ResolutionCode,
    string? ResolutionNote,
    DateTimeOffset? ResolvedAtUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? LastActionId)
{
    /// <summary>True when the incident is in a terminal (resolved) state.</summary>
    public bool IsClosed => State == IncidentState.Resolved;
}

/// <summary>Kind of notification the aggregator decided to emit for an incident transition.</summary>
public enum IncidentNotificationKind
{
    /// <summary>First event of an incident — delivered immediately.</summary>
    Initial = 0,
    /// <summary>Severity escalated to High/Critical — breaks the 10-minute silence once.</summary>
    Escalation = 1
}

/// <summary>A notification decision produced while aggregating an event into an incident.</summary>
public sealed record IncidentNotification(IncidentNotificationKind Kind, SecuritySeverity Severity);

/// <summary>
/// Outcome of <see cref="IncidentAggregator.Decide"/>: whether a new incident was created, the
/// resulting incident, and any notifications that should be delivered (respecting the silent window).
/// </summary>
public sealed record IncidentAggregateDecision(
    bool IsNew,
    Incident Incident,
    IReadOnlyList<IncidentNotification> Notifications);
