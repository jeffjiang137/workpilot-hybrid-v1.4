using System;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;

namespace WorkPilot.Domain.Automation.Run;

/// <summary>
/// A structured, safe run event (LOG-002/004). The <see cref="Sequence"/> is assigned by the
/// repository (monotonic per run, unique per run) at persistence time; the domain event is built
/// with <see cref="Create"/> (sequence 0) and the repository stamps the real sequence via
/// <see cref="WithSequence"/>. No secret or instruction body may enter this record — only codes,
/// message keys and safe property JSON.
/// </summary>
public sealed record RunEvent(
    RunEventId Id,
    RunId RunId,
    int Sequence,
    DateTimeOffset OccurredAtUtc,
    string Kind,
    RunEventLevel Level,
    string Code,
    string MessageKey,
    string SafePropertiesJson,
    string CorrelationId,
    StepRunId? StepId,
    int? Attempt)
{
    public static RunEvent Create(
        RunEventId id,
        RunId runId,
        string kind,
        RunEventLevel level,
        string code,
        string messageKey,
        string safePropertiesJson,
        string correlationId,
        DateTimeOffset occurredAtUtc,
        StepRunId? stepId = null,
        int? attempt = null)
    {
        if (string.IsNullOrWhiteSpace(kind))
            throw new DomainException(RunErrors.EventKindEmptyError());
        if (string.IsNullOrWhiteSpace(code))
            throw new DomainException(RunErrors.EventCodeEmptyError());
        if (string.IsNullOrWhiteSpace(messageKey))
            throw new DomainException(RunErrors.EventMessageKeyEmptyError());
        if (string.IsNullOrWhiteSpace(safePropertiesJson))
            throw new DomainException(RunErrors.EventPropertiesEmptyError());
        if (string.IsNullOrWhiteSpace(correlationId))
            throw new DomainException(RunErrors.EventCorrelationEmptyError());

        return new RunEvent(id, runId, 0, occurredAtUtc, kind, level, code, messageKey,
            safePropertiesJson, correlationId, stepId, attempt);
    }

    /// <summary>Returns a copy of this event stamped with the repository-assigned sequence.</summary>
    public RunEvent WithSequence(int sequence) => this with { Sequence = sequence };
}
