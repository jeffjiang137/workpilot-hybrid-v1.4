using System;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;

namespace WorkPilot.Domain.Automation.Run;

/// <summary>
/// A materialized trigger occurrence — one concrete firing of a trigger that may produce a run
/// (spec §3/§4). The <see cref="DedupeKey"/> is a stable SHA-256 so the scheduler can reject
/// duplicate materializations idempotently. Construct via <see cref="Create"/>.
/// </summary>
public sealed record TriggerOccurrence(
    TriggerOccurrenceId Id,
    AutomationId AutomationId,
    AutomationRevisionId AutomationRevisionId,
    string TriggerId,
    DateTimeOffset ScheduledAtUtc,
    DateTimeOffset MaterializedAtUtc,
    OccurrenceDisposition Disposition,
    string DedupeKey,
    int MissedCount,
    string SafeTriggerJson)
{
    public static TriggerOccurrence Create(
        TriggerOccurrenceId id,
        AutomationId automationId,
        AutomationRevisionId automationRevisionId,
        string triggerId,
        DateTimeOffset scheduledAtUtc,
        DateTimeOffset materializedAtUtc,
        OccurrenceDisposition disposition,
        string dedupeKey,
        int missedCount,
        string safeTriggerJson)
    {
        if (string.IsNullOrWhiteSpace(triggerId))
            throw new DomainException(RunErrors.OccurrenceTriggerIdEmptyError());
        if (dedupeKey.Length != 64)
            throw new DomainException(RunErrors.OccurrenceDedupeError());
        if (string.IsNullOrWhiteSpace(safeTriggerJson))
            throw new DomainException(RunErrors.OccurrenceTriggerJsonEmptyError());
        if (missedCount < 0)
            throw new DomainException(RunErrors.InvalidMissedCountError());

        return new TriggerOccurrence(id, automationId, automationRevisionId, triggerId, scheduledAtUtc,
            materializedAtUtc, disposition, dedupeKey, missedCount, safeTriggerJson);
    }
}
