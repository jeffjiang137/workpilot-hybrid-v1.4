using System.Collections.Generic;
using WorkPilot.Contracts.Primitives.Ids;

namespace WorkPilot.Domain.Security;

/// <summary>
/// An append-only, display-name-free security event (doc 06 §2). Contains only safe evidence
/// (field names, counts, hashes, error codes) — never business body, path, URL or secret.
/// The <see cref="Fingerprint"/> lets the aggregator collapse duplicates within a sliding window.
/// </summary>
public sealed record SecurityEvent(
    SecurityEventId Id,
    DateTimeOffset OccurredAtUtc,
    SecurityEventType Type,
    SecuritySeverity Severity,
    string Fingerprint,
    SourceReference? Source,
    AutomationId? AutomationId,
    RunId? RunId,
    IReadOnlyDictionary<string, string> SafeEvidence,
    string DetectorVersion)
{
    /// <summary>Capability stable id, if this event is capability-scoped (used in fingerprint).</summary>
    public string? CapabilityStableId =>
        SafeEvidence.TryGetValue("capability_stable_id", out var v) ? v : null;

    /// <summary>Primary error code, if any (used in fingerprint).</summary>
    public string? PrimaryErrorCode =>
        SafeEvidence.TryGetValue("primary_error_code", out var v) ? v : null;
}
