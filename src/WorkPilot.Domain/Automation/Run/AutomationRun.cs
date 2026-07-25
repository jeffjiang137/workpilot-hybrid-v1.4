using System;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;

namespace WorkPilot.Domain.Automation.Run;

/// <summary>
/// The durable run aggregate (RUN-002/003). A run is created atomically in <c>Queued</c> state with
/// a frozen <see cref="RunSnapshot"/>; claim/lease/execution transitions are driven by the
/// orchestrator (T09/T10/T13) which mutates this object and persists via the repository. Construct
/// new runs with <see cref="Create"/>; reconstruct from storage with the record constructor.
/// </summary>
public sealed record AutomationRun(
    RunId Id,
    AutomationRevisionId AutomationRevisionId,
    RunSnapshotId SnapshotId,
    RunTriggerKind TriggerKind,
    RunStatus Status,
    int Priority,
    DateTimeOffset ScheduledAtUtc,
    DateTimeOffset AvailableAtUtc,
    AutomationId? AutomationId,
    TriggerOccurrenceId? OccurrenceId,
    RunId? ParentRunId,
    DateTimeOffset? ClaimedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    string? LeaseOwner,
    DateTimeOffset? LeaseExpiresAtUtc,
    DateTimeOffset? CancellationRequestedAtUtc,
    string? CurrentNodeId,
    DateTimeOffset? ResumeAtUtc,
    int LastEventSequence,
    int ActiveDurationMs,
    int ModelTurnCount,
    int CapabilityCallCount,
    int ResultBytes,
    int CoalescedCount,
    int RecoveryCount,
    string? FinalErrorCode,
    int RowVersion,
    /// <summary>
    /// True when this run is a dry-run simulation: side-effecting executors (capability / agent /
    /// delay / notification) must short-circuit and produce a plan summary instead of performing any
    /// I/O (RUN-005 / AUT-A11). Dry-run runs are never persisted — the <see cref="DryRunPlanner"/>
    /// builds them in memory only — so reconstructed runs from storage are always <c>false</c>.
    /// </summary>
    bool IsDryRun = false)
{
    public static AutomationRun Create(
        RunId id,
        AutomationRevisionId automationRevisionId,
        RunSnapshotId snapshotId,
        RunTriggerKind triggerKind,
        DateTimeOffset scheduledAtUtc,
        DateTimeOffset availableAtUtc,
        AutomationId? automationId = null,
        TriggerOccurrenceId? occurrenceId = null,
        RunId? parentRunId = null,
        int priority = 0,
        bool isDryRun = false)
    {
        if (priority < -10 || priority > 10)
            throw new DomainException(RunErrors.InvalidPriorityError());

        return new AutomationRun(id, automationRevisionId, snapshotId, triggerKind, RunStatus.Queued,
            priority, scheduledAtUtc, availableAtUtc, automationId, occurrenceId, parentRunId,
            null, null, null, null, null, null, null, null, 0, 0, 0, 0, 0, 0, 0, null, 1, isDryRun);
    }

    /// <summary>True once the run has reached a terminal state and can no longer transition.</summary>
    public bool IsTerminal => Status is RunStatus.Completed or RunStatus.Failed
        or RunStatus.Cancelled or RunStatus.BlockedPolicy or RunStatus.NeedsReview;

    /// <summary>Worker claims a queued run. Throws if the run is not claimable (RUN-002).</summary>
    public AutomationRun MarkClaimed(string owner, DateTimeOffset leaseExpiresAt, DateTimeOffset now)
    {
        if (Status != RunStatus.Queued)
            throw new DomainException(RunErrors.AlreadyClaimedError());
        return this with
        {
            Status = RunStatus.Claimed,
            LeaseOwner = owner,
            LeaseExpiresAtUtc = leaseExpiresAt,
            ClaimedAtUtc = now,
            RowVersion = RowVersion + 1
        };
    }

    /// <summary>Run starts executing. Throws if already terminal.</summary>
    public AutomationRun MarkRunning(DateTimeOffset now)
    {
        if (IsTerminal)
            throw new DomainException(RunErrors.AlreadyTerminalError());
        return this with
        {
            Status = RunStatus.Running,
            StartedAtUtc = StartedAtUtc ?? now,
            RowVersion = RowVersion + 1
        };
    }

    /// <summary>Run completed successfully.</summary>
    public AutomationRun MarkCompleted(DateTimeOffset now)
        => this with { Status = RunStatus.Completed, FinishedAtUtc = FinishedAtUtc ?? now, RowVersion = RowVersion + 1 };

    /// <summary>Run failed with an optional final error code.</summary>
    public AutomationRun MarkFailed(DateTimeOffset now, string? errorCode = null)
        => this with
        {
            Status = RunStatus.Failed,
            FinishedAtUtc = FinishedAtUtc ?? now,
            FinalErrorCode = errorCode,
            RowVersion = RowVersion + 1
        };

    /// <summary>Records a cancellation request; the run is not yet cancelled until <see cref="ApplyCancellation"/>.</summary>
    public AutomationRun RequestCancellation(DateTimeOffset now)
        => this with { CancellationRequestedAtUtc = CancellationRequestedAtUtc ?? now, RowVersion = RowVersion + 1 };

    /// <summary>Run is cancelled (terminal).</summary>
    public AutomationRun ApplyCancellation(DateTimeOffset now)
        => this with
        {
            Status = RunStatus.Cancelled,
            FinishedAtUtc = FinishedAtUtc ?? now,
            CancellationRequestedAtUtc = CancellationRequestedAtUtc ?? now,
            RowVersion = RowVersion + 1
        };

    /// <summary>Run enters a delay wait; releases the worker slot but keeps the lease (RUN-004/Delay).</summary>
    public AutomationRun MarkWaitingDelay(DateTimeOffset resumeAtUtc, DateTimeOffset now)
    {
        if (Status != RunStatus.Running)
            throw new DomainException(RunErrors.IllegalRunTransitionError(Status, RunStatus.WaitingDelay));
        return this with
        {
            Status = RunStatus.WaitingDelay,
            CurrentNodeId = CurrentNodeId,
            ActiveDurationMs = ActiveDurationMs,
            ResumeAtUtc = resumeAtUtc,
            RowVersion = RowVersion + 1
        };
    }

    /// <summary>Run enters an approval wait; releases the worker slot but keeps the lease (RUN-004/Approval).</summary>
    public AutomationRun MarkWaitingApproval(DateTimeOffset now)
    {
        if (Status != RunStatus.Running)
            throw new DomainException(RunErrors.IllegalRunTransitionError(Status, RunStatus.WaitingApproval));
        return this with { Status = RunStatus.WaitingApproval, RowVersion = RowVersion + 1 };
    }

    /// <summary>Resumes from a wait back to <c>Queued</c> so the claimer re-picks it after re-evaluating epoch/policy.</summary>
    public AutomationRun ResumeFromWait()
    {
        if (Status != RunStatus.WaitingDelay && Status != RunStatus.WaitingApproval)
            throw new DomainException(RunErrors.IllegalRunTransitionError(Status, RunStatus.Queued));
        return this with { Status = RunStatus.Queued, ResumeAtUtc = null, RowVersion = RowVersion + 1 };
    }

    /// <summary>Lease expired before any side effect: drop back to <c>Queued</c> for re-claim (spec §7.1).</summary>
    public AutomationRun ExpireToQueued()
    {
        if (Status != RunStatus.Claimed)
            throw new DomainException(RunErrors.IllegalRunTransitionError(Status, RunStatus.Queued));
        return this with
        {
            Status = RunStatus.Queued,
            LeaseOwner = null,
            LeaseExpiresAtUtc = null,
            ClaimedAtUtc = null,
            RowVersion = RowVersion + 1
        };
    }

    /// <summary>Lease lost during an unknown write window: needs human review, never auto-replayed (spec §7.1).</summary>
    public AutomationRun ExpireToNeedsReview(DateTimeOffset now)
    {
        if (Status != RunStatus.Claimed && Status != RunStatus.Running)
            throw new DomainException(RunErrors.IllegalRunTransitionError(Status, RunStatus.NeedsReview));
        return this with
        {
            Status = RunStatus.NeedsReview,
            FinishedAtUtc = FinishedAtUtc ?? now,
            LeaseOwner = null,
            LeaseExpiresAtUtc = null,
            RowVersion = RowVersion + 1
        };
    }

    /// <summary>Crash recovery re-queues a claimed/running run for re-claim; bumps <see cref="RecoveryCount"/> (doc 04 §13).</summary>
    public AutomationRun RecoverToQueued(DateTimeOffset now)
    {
        if (Status != RunStatus.Claimed && Status != RunStatus.Running)
            throw new DomainException(RunErrors.IllegalRunTransitionError(Status, RunStatus.Queued));
        return this with
        {
            Status = RunStatus.Queued,
            LeaseOwner = null,
            LeaseExpiresAtUtc = null,
            ClaimedAtUtc = null,
            RecoveryCount = RecoveryCount + 1,
            RowVersion = RowVersion + 1
        };
    }

    /// <summary>Policy/permission blocked the run (terminal).</summary>
    public AutomationRun MarkBlockedPolicy(DateTimeOffset now, string? errorCode = null)
        => this with
        {
            Status = RunStatus.BlockedPolicy,
            FinishedAtUtc = FinishedAtUtc ?? now,
            FinalErrorCode = errorCode,
            RowVersion = RowVersion + 1
        };
}
