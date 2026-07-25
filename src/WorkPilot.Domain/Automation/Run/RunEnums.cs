namespace WorkPilot.Domain.Automation.Run;

/// <summary>Durable run lifecycle status. Storage strings are enforced by the 018 schema CHECK.</summary>
public enum RunStatus
{
    Queued,
    Claimed,
    Running,
    WaitingDelay,
    WaitingApproval,
    Completed,
    Failed,
    Cancelled,
    BlockedPolicy,
    NeedsReview
}

/// <summary>What triggered a run. Mirrors <c>TriggerType</c> storage strings (spec §3).</summary>
public enum RunTriggerKind
{
    Manual,
    Once,
    Interval,
    CalendarDaily,
    CalendarWeekly,
    CalendarMonthly,
    DomainEvent
}

/// <summary>Disposition of a materialized trigger occurrence (spec §3/§4).</summary>
public enum OccurrenceDisposition
{
    Queued,
    SkippedMissed,
    SkippedOverlap,
    Coalesced,
    Blocked
}

/// <summary>Per-step execution status (spec §3).</summary>
public enum StepRunStatus
{
    Pending,
    Ready,
    Running,
    WaitingDelay,
    WaitingApproval,
    Succeeded,
    Skipped,
    Failed,
    Cancelled,
    OutcomeUnknown,
    BlockedPolicy
}

/// <summary>Where in the side-effect pipeline a step currently is. <c>null</c> until prepared.</summary>
public enum SideEffectPhase
{
    Prepared,
    PermitIssued,
    RequestSending,
    ResponseReceived,
    Persisted
}

/// <summary>Severity of a structured run event (spec §3 LOG-002/004).</summary>
public enum RunEventLevel
{
    Trace,
    Info,
    Warning,
    Error,
    Security
}
