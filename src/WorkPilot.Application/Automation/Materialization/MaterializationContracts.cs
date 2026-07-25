using System.Collections.Generic;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation;
using WorkPilot.Domain.Automation.Run;
using WorkPilot.Domain.Automation.Scheduling;

namespace WorkPilot.Application.Automation.Materialization;

/// <summary>Outcome of one materialization attempt for a candidate trigger occurrence.</summary>
public enum MaterializeOutcome
{
    Created,
    AlreadyMaterialized,
    SkippedOverlap,
    Coalesced,
    Blocked
}

/// <summary>A due schedule the materializer should process (spec doc 04 §2/§3).</summary>
public sealed record DueSchedule(
    AutomationId AutomationId,
    AutomationRevisionId AutomationRevisionId,
    string TriggerId,
    DateTimeOffset? LastMaterializedAtUtc,
    DateTimeOffset? NextOccurrenceAtUtc);

/// <summary>A pending domain-event outbox row the dispatcher should match to triggers (spec doc 04 §4).</summary>
public sealed record PendingOutboxEvent(
    string Id,
    string EventType,
    string SpaceId,
    string EntityType,
    string EntityId,
    int EntityVersion,
    string SafePayloadJson,
    DateTimeOffset OccurredAtUtc,
    int AttemptCount);

/// <summary>A run whose lease has expired and must be recovered (spec doc 04 §6/§13).</summary>
public sealed record ExpiredLease(
    RunId RunId,
    RunStatus Status,
    bool SideEffectInFlight,
    int RecoveryCount);

/// <summary>Summary of one materialization tick (for diagnostics / tests).</summary>
public sealed record MaterializationBatchResult(int SchedulesProcessed, int RunsCreated, int OccurrencesSkipped);

/// <summary>Summary of one claim tick (for diagnostics / tests).</summary>
public sealed record ClaimBatchResult(int Claimed, IReadOnlyList<RunId> ClaimedRunIds, int Recovered);
