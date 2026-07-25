using WorkPilot.Contracts.Primitives.Ids;

namespace WorkPilot.Domain.Automation.Scheduling;

/// <summary>A concrete scheduled occurrence with DST bookkeeping flags (spec doc 04 §2.2).</summary>
public sealed record ScheduledOccurrence(
    DateTimeOffset Utc,
    bool DstAdjustedForward,
    bool DstAmbiguousFirst);

/// <summary>
/// Result of <see cref="ScheduleCalculator.ComputeNext"/>. Discriminated by <see cref="HasOccurrence"/>:
/// <see cref="NoScheduledTime"/> (manual/domain-event/disabled/past), <see cref="Found"/>, or
/// <see cref="ErrorCode"/> set (e.g. unknown time zone). Reused by preview and materializer so they
/// never diverge (T05 DoD: "预览与调度共用同一算法").
/// </summary>
public sealed record NextOccurrenceResult(
    bool HasOccurrence,
    ScheduledOccurrence? Occurrence,
    string? ErrorCode,
    IReadOnlyDictionary<string, string>? SafeDetails)
{
    public static NextOccurrenceResult NoScheduledTime() => new(false, null, null, null);
    public static NextOccurrenceResult Found(ScheduledOccurrence o) => new(true, o, null, null);
    public static NextOccurrenceResult Error(string code, IReadOnlyDictionary<string, string>? details = null)
        => new(false, null, code, details);
}

/// <summary>Outcome of a missed-run resolution (spec doc 04 §3).</summary>
public sealed record MissedRunResult(
    IReadOnlyList<DateTimeOffset> Occurrences,
    int SkippedCount,
    DateTimeOffset? LastCandidateUtc);

/// <summary>Coarse lifecycle category of an existing run, from the materializer's perspective.</summary>
public enum RunStatusCategory
{
    Terminal,
    Queued,
    Active
}

/// <summary>Summary of an in-flight run needed to decide overlap policy (RUN-009).</summary>
public sealed record ExistingRunSummary(
    RunId Id,
    RunStatusCategory Status,
    bool CancellationRequested);

/// <summary>Decision produced by <see cref="OverlapPolicyEvaluator"/> for one new candidate.</summary>
public enum OverlapDecisionKind
{
    Create,
    Skip,
    Coalesce,
    CancelPreviousAndCreate
}

public sealed record OverlapDecision(
    OverlapDecisionKind Kind,
    RunId? CoalesceTargetId = null,
    int CoalescedCount = 0,
    IReadOnlyList<RunId>? CancellationTargetIds = null);
