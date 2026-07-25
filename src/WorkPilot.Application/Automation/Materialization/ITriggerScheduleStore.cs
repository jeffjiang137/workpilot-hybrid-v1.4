using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation;

namespace WorkPilot.Application.Automation.Materialization;

/// <summary>
/// Persistence port for trigger schedules (spec doc 04 §2/§3). A schedule row is created when a
/// revision is published and tracks the last materialized instant and the next due instant used by
/// the due-index. Implemented by Infrastructure against <c>automation_schedules</c>.
/// </summary>
public interface ITriggerScheduleStore
{
    /// <summary>Creates or replaces the schedule row for one trigger of a published revision.</summary>
    Task<Result> UpsertAsync(
        AutomationId automationId,
        AutomationRevisionId revisionId,
        TriggerDefinition trigger,
        DateTimeOffset? nextOccurrenceAtUtc,
        DateTimeOffset now,
        CancellationToken ct);

    /// <summary>Returns enabled schedules whose next due instant is at/before <paramref name="now"/> (bounded batch).</summary>
    Task<Result<IReadOnlyList<DueSchedule>>> GetDueSchedulesAsync(DateTimeOffset now, int batchSize, CancellationToken ct);

    /// <summary>Advances a schedule's materialized pointer and recomputes its next-due hint.</summary>
    Task<Result> UpdatePointerAsync(
        AutomationId automationId,
        AutomationRevisionId revisionId,
        string triggerId,
        DateTimeOffset lastMaterializedAtUtc,
        DateTimeOffset? nextOccurrenceAtUtc,
        CancellationToken ct);

    /// <summary>Enabled domain-event triggers in a space matching an event type (spec doc 04 §4).</summary>
    Task<Result<IReadOnlyList<DueSchedule>>> GetDomainEventSchedulesAsync(string spaceId, string eventType, CancellationToken ct);
}
