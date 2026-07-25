namespace WorkPilot.Domain.Automation.Run;

/// <summary>
/// Explicit mapping between the C# (PascalCase) run enums and their lowercase+underscore storage
/// strings, which the v1.5 018 schema enforces via CHECK constraints. Kept in one place to avoid
/// ad-hoc <c>ToString()</c>/<c>Enum.Parse</c> that would violate those constraints.
/// </summary>
public static class RunStorageMaps
{
    public static string ToStorage(this RunStatus s) => s switch
    {
        RunStatus.Queued => "queued",
        RunStatus.Claimed => "claimed",
        RunStatus.Running => "running",
        RunStatus.WaitingDelay => "waiting_delay",
        RunStatus.WaitingApproval => "waiting_approval",
        RunStatus.Completed => "completed",
        RunStatus.Failed => "failed",
        RunStatus.Cancelled => "cancelled",
        RunStatus.BlockedPolicy => "blocked_policy",
        RunStatus.NeedsReview => "needs_review",
        _ => throw new System.ArgumentOutOfRangeException(nameof(s), s, "Unknown run status")
    };

    public static RunStatus StatusFromStorage(string s) => s switch
    {
        "queued" => RunStatus.Queued,
        "claimed" => RunStatus.Claimed,
        "running" => RunStatus.Running,
        "waiting_delay" => RunStatus.WaitingDelay,
        "waiting_approval" => RunStatus.WaitingApproval,
        "completed" => RunStatus.Completed,
        "failed" => RunStatus.Failed,
        "cancelled" => RunStatus.Cancelled,
        "blocked_policy" => RunStatus.BlockedPolicy,
        "needs_review" => RunStatus.NeedsReview,
        _ => throw new System.ArgumentOutOfRangeException(nameof(s), s, "Unknown run status storage value")
    };

    public static string ToStorage(this RunTriggerKind k) => k switch
    {
        RunTriggerKind.Manual => "manual",
        RunTriggerKind.Once => "once",
        RunTriggerKind.Interval => "interval",
        RunTriggerKind.CalendarDaily => "calendar_daily",
        RunTriggerKind.CalendarWeekly => "calendar_weekly",
        RunTriggerKind.CalendarMonthly => "calendar_monthly",
        RunTriggerKind.DomainEvent => "domain_event",
        _ => throw new System.ArgumentOutOfRangeException(nameof(k), k, "Unknown trigger kind")
    };

    public static RunTriggerKind TriggerKindFromStorage(string s) => s switch
    {
        "manual" => RunTriggerKind.Manual,
        "once" => RunTriggerKind.Once,
        "interval" => RunTriggerKind.Interval,
        "calendar_daily" => RunTriggerKind.CalendarDaily,
        "calendar_weekly" => RunTriggerKind.CalendarWeekly,
        "calendar_monthly" => RunTriggerKind.CalendarMonthly,
        "domain_event" => RunTriggerKind.DomainEvent,
        _ => throw new System.ArgumentOutOfRangeException(nameof(s), s, "Unknown trigger kind storage value")
    };

    public static string ToStorage(this OccurrenceDisposition d) => d switch
    {
        OccurrenceDisposition.Queued => "queued",
        OccurrenceDisposition.SkippedMissed => "skipped_missed",
        OccurrenceDisposition.SkippedOverlap => "skipped_overlap",
        OccurrenceDisposition.Coalesced => "coalesced",
        OccurrenceDisposition.Blocked => "blocked",
        _ => throw new System.ArgumentOutOfRangeException(nameof(d), d, "Unknown occurrence disposition")
    };

    public static OccurrenceDisposition DispositionFromStorage(string s) => s switch
    {
        "queued" => OccurrenceDisposition.Queued,
        "skipped_missed" => OccurrenceDisposition.SkippedMissed,
        "skipped_overlap" => OccurrenceDisposition.SkippedOverlap,
        "coalesced" => OccurrenceDisposition.Coalesced,
        "blocked" => OccurrenceDisposition.Blocked,
        _ => throw new System.ArgumentOutOfRangeException(nameof(s), s, "Unknown occurrence disposition storage value")
    };

    public static string ToStorage(this StepRunStatus s) => s switch
    {
        StepRunStatus.Pending => "pending",
        StepRunStatus.Ready => "ready",
        StepRunStatus.Running => "running",
        StepRunStatus.WaitingDelay => "waiting_delay",
        StepRunStatus.WaitingApproval => "waiting_approval",
        StepRunStatus.Succeeded => "succeeded",
        StepRunStatus.Skipped => "skipped",
        StepRunStatus.Failed => "failed",
        StepRunStatus.Cancelled => "cancelled",
        StepRunStatus.OutcomeUnknown => "outcome_unknown",
        StepRunStatus.BlockedPolicy => "blocked_policy",
        _ => throw new System.ArgumentOutOfRangeException(nameof(s), s, "Unknown step run status")
    };

    public static StepRunStatus StepStatusFromStorage(string s) => s switch
    {
        "pending" => StepRunStatus.Pending,
        "ready" => StepRunStatus.Ready,
        "running" => StepRunStatus.Running,
        "waiting_delay" => StepRunStatus.WaitingDelay,
        "waiting_approval" => StepRunStatus.WaitingApproval,
        "succeeded" => StepRunStatus.Succeeded,
        "skipped" => StepRunStatus.Skipped,
        "failed" => StepRunStatus.Failed,
        "cancelled" => StepRunStatus.Cancelled,
        "outcome_unknown" => StepRunStatus.OutcomeUnknown,
        "blocked_policy" => StepRunStatus.BlockedPolicy,
        _ => throw new System.ArgumentOutOfRangeException(nameof(s), s, "Unknown step run status storage value")
    };

    public static string? ToStorage(this SideEffectPhase? p) => p switch
    {
        null => null,
        SideEffectPhase.Prepared => "prepared",
        SideEffectPhase.PermitIssued => "permit_issued",
        SideEffectPhase.RequestSending => "request_sending",
        SideEffectPhase.ResponseReceived => "response_received",
        SideEffectPhase.Persisted => "persisted",
        _ => throw new System.ArgumentOutOfRangeException(nameof(p), p, "Unknown side-effect phase")
    };

    public static SideEffectPhase? SideEffectPhaseFromStorage(string? s) => s switch
    {
        null => null,
        "prepared" => SideEffectPhase.Prepared,
        "permit_issued" => SideEffectPhase.PermitIssued,
        "request_sending" => SideEffectPhase.RequestSending,
        "response_received" => SideEffectPhase.ResponseReceived,
        "persisted" => SideEffectPhase.Persisted,
        _ => throw new System.ArgumentOutOfRangeException(nameof(s), s, "Unknown side-effect phase storage value")
    };

    public static string ToStorage(this RunEventLevel l) => l switch
    {
        RunEventLevel.Trace => "trace",
        RunEventLevel.Info => "info",
        RunEventLevel.Warning => "warning",
        RunEventLevel.Error => "error",
        RunEventLevel.Security => "security",
        _ => throw new System.ArgumentOutOfRangeException(nameof(l), l, "Unknown run event level")
    };

    public static RunEventLevel EventLevelFromStorage(string s) => s switch
    {
        "trace" => RunEventLevel.Trace,
        "info" => RunEventLevel.Info,
        "warning" => RunEventLevel.Warning,
        "error" => RunEventLevel.Error,
        "security" => RunEventLevel.Security,
        _ => throw new System.ArgumentOutOfRangeException(nameof(s), s, "Unknown run event level storage value")
    };
}
