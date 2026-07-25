using System.Collections.Generic;

namespace WorkPilot.Contracts.Primitives;

/// <summary>Severity for the diagnostic log channel (doc 05 §5).</summary>
public enum DiagnosticLogLevel
{
    Trace,
    Information,
    Warning,
    Error
}

/// <summary>
/// One structured diagnostic record. The <see cref="Safe"/> bag holds only safe scalars — no exception
/// messages, paths, or secrets (doc 05 §5). The redaction pipeline is applied to the serialized line.
/// </summary>
public sealed record DiagnosticEvent(
    string Event,
    DiagnosticLogLevel Level,
    string CorrelationId,
    IReadOnlyDictionary<string, object?>? Safe = null,
    string? MessageKey = null);

/// <summary>
/// Injection port for diagnostic logging (doc 05 §5, LOG-A06/A08/A09). Replaces the V1.4 static
/// <c>AppLogger</c>; a compatible facade (<c>AppDiagnostics</c>) forwards to the active implementation.
/// Implementations must never block a capability send and must degrade gracefully on I/O failure.
/// </summary>
public interface IDiagnosticLogger
{
    /// <summary>Enqueue a diagnostic event. Low-severity events may be dropped under backpressure.</summary>
    void Emit(DiagnosticEvent evt);

    /// <summary>True when the logger is in a degraded state (last I/O failed) — UI shows "Logging Degraded".</summary>
    bool IsDegraded { get; }
}
