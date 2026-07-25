using System;
using System.Collections.Generic;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Security;
using Xunit;

namespace WorkPilot.Domain.Tests.Security;

public sealed class IncidentAggregatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly IIdGenerator Ids = new SequentialIdGenerator();

    private static SecurityEvent Event(
        SecurityEventType type,
        SecuritySeverity severity,
        SourceReference? source = null,
        string? capability = null,
        AutomationId? automation = null,
        string? primaryError = null,
        DateTimeOffset? at = null,
        IReadOnlyDictionary<string, string>? evidence = null)
    {
        var ev = new Dictionary<string, string>(evidence ?? new Dictionary<string, string>());
        if (capability is not null) ev["capability_stable_id"] = capability;
        if (primaryError is not null) ev["primary_error_code"] = primaryError;

        var fp = SecurityEventFingerprint.Compute(type, source, capability, automation, primaryError);
        return new SecurityEvent(
            SecurityEventId.Create(Ids), at ?? T0, type, severity, fp, source, automation, null, ev,
            "1.0.0");
    }

    [Fact]
    public void Fingerprint_is_deterministic_and_excludes_display_names()
    {
        var a = SecurityEventFingerprint.Compute(SecurityEventType.AuthFailureContinuous,
            new SourceReference("connector", "github"), null, null, "AUTH_42");
        var b = SecurityEventFingerprint.Compute(SecurityEventType.AuthFailureContinuous,
            new SourceReference("connector", "github"), null, null, "AUTH_42");

        Assert.Equal(a, b);
        // Fingerprint must not embed a display name / path / secret.
        Assert.DoesNotContain("GitHub", a, StringComparison.Ordinal);
        Assert.DoesNotContain("token", a, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void First_event_opens_new_incident_with_initial_notification()
    {
        var e = Event(SecurityEventType.AuthFailureContinuous, SecuritySeverity.High);
        var d = IncidentAggregator.Decide(null, e, T0, IncidentId.Create(Ids));

        Assert.True(d.IsNew);
        Assert.Equal(1, d.Incident.Count);
        Assert.Equal(IncidentState.Open, d.Incident.State);
        Assert.Single(d.Notifications);
        Assert.Equal(IncidentNotificationKind.Initial, d.Notifications[0].Kind);
    }

    [Fact]
    public void Same_fingerprint_within_window_merges_silently()
    {
        var e1 = Event(SecurityEventType.AuthFailureContinuous, SecuritySeverity.High);
        var first = IncidentAggregator.Decide(null, e1, T0, IncidentId.Create(Ids));

        var e2 = Event(SecurityEventType.AuthFailureContinuous, SecuritySeverity.High,
            at: T0 + TimeSpan.FromMinutes(2));
        var second = IncidentAggregator.Decide(first.Incident, e2, T0 + TimeSpan.FromMinutes(2), IncidentId.Create(Ids));

        Assert.False(second.IsNew);
        Assert.Equal(2, second.Incident.Count);
        Assert.Equal(IncidentState.Open, second.Incident.State);
        Assert.Empty(second.Notifications); // silent window: no storm
    }

    [Fact]
    public void Severity_escalation_within_window_breaks_silence_once()
    {
        var e1 = Event(SecurityEventType.AuthFailureContinuous, SecuritySeverity.Low);
        var first = IncidentAggregator.Decide(null, e1, T0, IncidentId.Create(Ids));

        var e2 = Event(SecurityEventType.AuthFailureContinuous, SecuritySeverity.Critical,
            at: T0 + TimeSpan.FromMinutes(1));
        var second = IncidentAggregator.Decide(first.Incident, e2, T0 + TimeSpan.FromMinutes(1), IncidentId.Create(Ids));

        Assert.Equal(SecuritySeverity.Critical, second.Incident.Severity);
        Assert.Single(second.Notifications);
        Assert.Equal(IncidentNotificationKind.Escalation, second.Notifications[0].Kind);
    }

    [Fact]
    public void Severity_never_lowers_on_merge()
    {
        var e1 = Event(SecurityEventType.AuthFailureContinuous, SecuritySeverity.Critical);
        var first = IncidentAggregator.Decide(null, e1, T0, IncidentId.Create(Ids));

        var e2 = Event(SecurityEventType.AuthFailureContinuous, SecuritySeverity.Low,
            at: T0 + TimeSpan.FromMinutes(1));
        var second = IncidentAggregator.Decide(first.Incident, e2, T0 + TimeSpan.FromMinutes(1), IncidentId.Create(Ids));

        Assert.Equal(SecuritySeverity.Critical, second.Incident.Severity);
    }

    [Fact]
    public void Evidence_digests_capped_at_20_distinct()
    {
        Incident? inc = null;
        var now = T0;
        for (var i = 0; i < 50; i++)
        {
            var ev = Event(SecurityEventType.AuthFailureContinuous, SecuritySeverity.Medium,
                evidence: new Dictionary<string, string> { ["n"] = i.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                at: now);
            var d = IncidentAggregator.Decide(inc, ev, now, IncidentId.Create(Ids));
            inc = d.Incident;
            now += TimeSpan.FromMinutes(1);
        }

        Assert.Equal(50, inc!.Count);
        Assert.Equal(20, inc.RecentEvidenceDigests.Count);
    }

    [Fact]
    public void Resolved_incident_reopens_on_fresh_same_fingerprint()
    {
        var e1 = Event(SecurityEventType.AuthFailureContinuous, SecuritySeverity.High);
        var first = IncidentAggregator.Decide(null, e1, T0, IncidentId.Create(Ids));
        var resolved = first.Incident with { State = IncidentState.Resolved, ResolvedAtUtc = T0 + TimeSpan.FromMinutes(1) };

        var e2 = Event(SecurityEventType.AuthFailureContinuous, SecuritySeverity.High,
            at: T0 + TimeSpan.FromMinutes(5));
        var second = IncidentAggregator.Decide(resolved, e2, T0 + TimeSpan.FromMinutes(5), IncidentId.Create(Ids));

        Assert.Equal(IncidentState.Reopened, second.Incident.State);
        Assert.Equal(2, second.Incident.Count);
        Assert.Null(second.Incident.ResolutionCode);
        Assert.Single(second.Notifications);
        Assert.Equal(IncidentNotificationKind.Initial, second.Notifications[0].Kind);
    }

    [Fact]
    public void Outside_window_opens_new_incident()
    {
        var e1 = Event(SecurityEventType.AuthFailureContinuous, SecuritySeverity.High);
        var first = IncidentAggregator.Decide(null, e1, T0, IncidentId.Create(Ids));

        var e2 = Event(SecurityEventType.AuthFailureContinuous, SecuritySeverity.High,
            at: T0 + TimeSpan.FromMinutes(30));
        var second = IncidentAggregator.Decide(first.Incident, e2, T0 + TimeSpan.FromMinutes(30), IncidentId.Create(Ids));

        Assert.True(second.IsNew);
        Assert.Equal(1, second.Incident.Count);
    }
}

public sealed class SeverityCalculatorTests
{
    [Fact]
    public void Modifiers_only_raise_never_lower()
    {
        var s = SeverityCalculator.Compute(SecuritySeverity.Medium,
            involvesCredential: false, involvesExecutable: false, involvesAudit: false, involvesRedaction: false,
            affectedAutomationCount: 1, externalSideEffectUnknownResult: false, evidenceIncomplete: false);
        Assert.Equal(SecuritySeverity.Medium, s);
    }

    [Fact]
    public void Three_affected_automations_raises_one()
    {
        var s = SeverityCalculator.Compute(SecuritySeverity.Low,
            false, false, false, false, affectedAutomationCount: 3, false, false);
        Assert.Equal(SecuritySeverity.Medium, s);
    }

    [Fact]
    public void Credential_involvement_at_least_high()
    {
        var s = SeverityCalculator.Compute(SecuritySeverity.Info,
            involvesCredential: true, involvesExecutable: false, involvesAudit: false, involvesRedaction: false,
            affectedAutomationCount: 0, false, false);
        Assert.Equal(SecuritySeverity.High, s);
    }

    [Fact]
    public void Incomplete_evidence_never_promoted_to_critical_by_modifiers()
    {
        var s = SeverityCalculator.Compute(SecuritySeverity.High,
            involvesCredential: false, involvesExecutable: false, involvesAudit: false, involvesRedaction: false,
            affectedAutomationCount: 5, externalSideEffectUnknownResult: false, evidenceIncomplete: true);
        Assert.Equal(SecuritySeverity.High, s);
    }

    [Fact]
    public void Severity_capped_at_critical()
    {
        var s = SeverityCalculator.Compute(SecuritySeverity.Critical,
            involvesCredential: true, involvesExecutable: true, involvesAudit: true, involvesRedaction: true,
            affectedAutomationCount: 10, externalSideEffectUnknownResult: true, evidenceIncomplete: false);
        Assert.Equal(SecuritySeverity.Critical, s);
    }
}
