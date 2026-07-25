using System.Collections.Generic;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Security;

namespace WorkPilot.Domain.Security.Detectors;

/// <summary>A finding produced by a detector rule: the event to emit plus an optional idempotent action.</summary>
public sealed record DetectorFinding(SecurityEvent Event, DetectorAction? Action);

/// <summary>
/// A single fixed detection rule (doc 06 §4). Pure: given a <see cref="DetectorContext"/> it returns
/// zero or more <see cref="DetectorFinding"/>s. No I/O, no clock, no randomness.
/// </summary>
public interface IDetectorRule
{
    string Id { get; }
    SecurityEventType EventType { get; }
    IReadOnlyList<DetectorFinding> Evaluate(DetectorContext ctx);
}

/// <summary>
/// Builds a <see cref="SecurityEvent"/> for a detector: computes the display-name-free fingerprint and
/// the effective severity (base severity raised by the doc 06 §5 modifiers, never lowered).
/// </summary>
public static class DetectorEventBuilder
{
    public static SecurityEvent Build(
        IIdGenerator ids,
        SecurityEventType type,
        SecuritySeverity baseSeverity,
        SourceReference? source,
        AutomationId? automationId,
        string? capabilityStableId,
        string? primaryErrorCode,
        System.DateTimeOffset occurredAt,
        IReadOnlyDictionary<string, string> evidence,
        bool involvesCredential = false,
        bool involvesExecutable = false,
        bool involvesAudit = false,
        bool involvesRedaction = false,
        bool externalSideEffectUnknown = false,
        bool evidenceIncomplete = false,
        int affectedAutomationCount = 1)
    {
        var severity = SeverityCalculator.Compute(
            baseSeverity, involvesCredential, involvesExecutable, involvesAudit, involvesRedaction,
            affectedAutomationCount, externalSideEffectUnknown, evidenceIncomplete);

        var fp = SecurityEventFingerprint.Compute(type, source, capabilityStableId, automationId, primaryErrorCode);
        return new SecurityEvent(
            SecurityEventId.Create(ids), occurredAt, type, severity, fp,
            source, automationId, null, evidence, DetectorConstants.DetectorVersion);
    }
}
