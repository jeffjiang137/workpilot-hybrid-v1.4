using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using WorkPilot.Contracts.Primitives.Ids;

namespace WorkPilot.Domain.Security;

/// <summary>
/// Pure aggregation logic for security incidents (doc 06 §3). Given an existing incident (or none)
/// and a new event, it decides whether to open a new incident or merge into the open one, computes
/// the resulting incident, and emits the notifications that respect the 10-minute silent window.
/// No I/O, no clock dependency beyond the <paramref name="now"/> passed in.
/// </summary>
public static class IncidentAggregator
{
    /// <summary>Sliding window during which same-fingerprint events merge (doc 06 §3).</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(10);

    /// <summary>Maximum distinct recent evidence digests retained per incident (doc 06 §3).</summary>
    public static readonly int MaxEvidenceDigests = 20;

    public static IncidentAggregateDecision Decide(
        Incident? existing,
        SecurityEvent e,
        DateTimeOffset now,
        IncidentId newIncidentId,
        TimeSpan? window = null)
    {
        var w = window ?? DefaultWindow;

        if (existing is null)
            return OpenNew(e, now, newIncidentId);

        // A fresh same-fingerprint event after the incident was resolved re-opens it (doc 06 §3).
        if (existing.State == IncidentState.Resolved)
            return Reopen(existing, e, now, newIncidentId);

        // Outside the sliding window the open incident is stale: a new incident is opened.
        if (existing.LastSeenUtc + w < now)
            return OpenNew(e, now, newIncidentId);

        return Merge(existing, e, now);
    }

    private static IncidentAggregateDecision OpenNew(SecurityEvent e, DateTimeOffset now, IncidentId newIncidentId)
    {
        var inc = new Incident(
            Id: newIncidentId,
            Fingerprint: e.Fingerprint,
            State: IncidentState.Open,
            Severity: e.Severity,
            Type: e.Type,
            FirstSeenUtc: now,
            LastSeenUtc: now,
            Count: 1,
            RecentEvidenceDigests: new[] { EvidenceDigest(e) },
            ResolutionCode: null,
            ResolutionNote: null,
            ResolvedAtUtc: null,
            CreatedAtUtc: now,
            UpdatedAtUtc: now,
            LastActionId: null);

        return new IncidentAggregateDecision(true, inc, new[] { new IncidentNotification(IncidentNotificationKind.Initial, e.Severity) });
    }

    private static IncidentAggregateDecision Reopen(Incident existing, SecurityEvent e, DateTimeOffset now, IncidentId newIncidentId)
    {
        var inc = existing with
        {
            Id = newIncidentId,
            State = IncidentState.Reopened,
            Severity = SeverityCalculator.Max(existing.Severity, e.Severity),
            Type = e.Type,
            LastSeenUtc = now,
            Count = existing.Count + 1,
            RecentEvidenceDigests = MergeEvidence(existing.RecentEvidenceDigests, e),
            ResolutionCode = null,
            ResolutionNote = null,
            ResolvedAtUtc = null,
            UpdatedAtUtc = now
        };

        // Re-opening is a fresh alert: notify immediately and persist as a new incident row.
        return new IncidentAggregateDecision(true, inc, new[] { new IncidentNotification(IncidentNotificationKind.Initial, inc.Severity) });
    }

    private static IncidentAggregateDecision Merge(Incident existing, SecurityEvent e, DateTimeOffset now)
    {
        var priorSeverity = existing.Severity;
        var severity = SeverityCalculator.Max(existing.Severity, e.Severity);
        var escalated = (int)severity > (int)priorSeverity
                        && (int)severity >= (int)SecuritySeverity.High
                        && (int)priorSeverity < (int)SecuritySeverity.High;

        var inc = existing with
        {
            Severity = severity,
            Type = e.Type,
            LastSeenUtc = now,
            Count = existing.Count + 1,
            RecentEvidenceDigests = MergeEvidence(existing.RecentEvidenceDigests, e),
            UpdatedAtUtc = now
        };

        var notifications = new List<IncidentNotification>();
        // Within the silent window only an escalation to High/Critical breaks silence once.
        if (escalated)
            notifications.Add(new IncidentNotification(IncidentNotificationKind.Escalation, severity));

        return new IncidentAggregateDecision(false, inc, notifications);
    }

    private static IReadOnlyList<string> MergeEvidence(IReadOnlyList<string> existing, SecurityEvent e)
    {
        var digest = EvidenceDigest(e);
        var seen = new HashSet<string>(StringComparer.Ordinal) { digest };
        var result = new List<string> { digest };

        foreach (var d in existing)
        {
            if (result.Count >= MaxEvidenceDigests) break;
            if (seen.Add(d)) result.Add(d);
        }

        return result;
    }

    private static string EvidenceDigest(SecurityEvent e)
    {
        var sb = new StringBuilder();
        sb.Append(e.OccurredAtUtc.UtcTicks.ToString("D20")).Append('|');
        foreach (var kv in e.SafeEvidence.OrderBy(k => k.Key, StringComparer.Ordinal))
            sb.Append(kv.Key).Append('=').Append(kv.Value).Append('|');
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
