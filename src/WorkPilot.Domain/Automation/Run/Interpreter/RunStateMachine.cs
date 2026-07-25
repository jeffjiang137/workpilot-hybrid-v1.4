using System;
using System.Collections.Generic;
using WorkPilot.Contracts.Primitives;

namespace WorkPilot.Domain.Automation.Run.Interpreter;

/// <summary>
/// Run-level lifecycle state machine (spec doc 03 §7.1). Encodes the legal transitions as a static
/// adjacency table and exposes <see cref="TryTransition"/> which validates the proposed transition
/// against the run's <em>current</em> status and <c>RowVersion</c> (CAS guard). Illegal transitions
/// return <see cref="Result.IsSuccess"/> = false with <see cref="RunErrors.StateTransitionRejected"/>
/// and must never mutate the run — callers are expected to emit an internal security event.
/// </summary>
public static class RunStateMachine
{
    private static readonly Dictionary<RunStatus, HashSet<RunStatus>> LegalTransitions = new()
    {
        [RunStatus.Queued] = new() { RunStatus.Claimed, RunStatus.Running, RunStatus.Cancelled, RunStatus.BlockedPolicy },
        [RunStatus.Claimed] = new()
        {
            RunStatus.Running, RunStatus.Queued,            // lease expired before side effect
            RunStatus.NeedsReview,                          // lease lost during unknown write window
            RunStatus.Cancelled, RunStatus.BlockedPolicy
        },
        [RunStatus.Running] = new()
        {
            RunStatus.WaitingDelay, RunStatus.WaitingApproval,
            RunStatus.Completed, RunStatus.Failed, RunStatus.Cancelled,
            RunStatus.BlockedPolicy, RunStatus.NeedsReview
        },
        [RunStatus.WaitingDelay] = new() { RunStatus.Queued, RunStatus.Cancelled, RunStatus.BlockedPolicy },
        [RunStatus.WaitingApproval] = new() { RunStatus.Queued, RunStatus.Cancelled, RunStatus.BlockedPolicy },
        // Terminal states have no outbound transitions.
        [RunStatus.Completed] = new(),
        [RunStatus.Failed] = new(),
        [RunStatus.Cancelled] = new(),
        [RunStatus.BlockedPolicy] = new(),
        [RunStatus.NeedsReview] = new()
    };

    /// <summary>True if <paramref name="to"/> is a legal successor of <paramref name="from"/>.</summary>
    public static bool CanTransition(RunStatus from, RunStatus to)
        => LegalTransitions.TryGetValue(from, out var outs) && outs.Contains(to);

    /// <summary>
    /// Validates a proposed run transition. On success returns <see cref="Result.Success()"/>; on
    /// failure returns a rejected <see cref="Result"/> (no mutation). The <paramref name="expectedRowVersion"/>
    /// is the CAS guard: a mismatch means a concurrent writer moved the run and the transition must be
    /// re-evaluated (never blindly applied).
    /// </summary>
    public static Result TryTransition(RunStatus current, int currentRowVersion, int expectedRowVersion,
        RunStatus proposed, out bool legal)
    {
        legal = CanTransition(current, proposed);
        if (!legal)
            return Result.Failure(RunErrors.StateTransitionRejectedError(current, proposed));
        if (currentRowVersion != expectedRowVersion)
            return Result.Failure(RunErrors.ConcurrencyConflictError());
        return Result.Success();
    }
}
