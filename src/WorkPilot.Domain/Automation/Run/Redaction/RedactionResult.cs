using System.Collections.Generic;

namespace WorkPilot.Domain.Automation.Run.Redaction;

/// <summary>
/// Outcome of redaction (doc 05 §4). The pipeline returns this, never a bare string, so callers can
/// detect truncation and surface violation codes (e.g. canary leak, redaction failure).
/// </summary>
public sealed record RedactionResult(
    string Value,
    int RedactionCount,
    bool Truncated,
    IReadOnlyList<string> ViolationCodes)
{
    public static readonly RedactionResult Empty = new("", 0, false, Array.Empty<string>());

    public bool HasViolation => ViolationCodes.Count > 0;
}
