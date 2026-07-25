using System;
using System.Collections.Generic;
using WorkPilot.Contracts.Primitives;

namespace WorkPilot.Domain.Automation.Run.Interpreter;

/// <summary>
/// Per-step lifecycle state machine (spec doc 03 §7.2). Each transition must be checked with
/// <see cref="TryTransition"/> which validates the proposed <see cref="StepRunStatus"/> against the
/// step's current status and <c>RowVersion</c> (CAS guard). Illegal transitions return a rejected
/// <see cref="Result"/> without mutating the step, so a concurrent writer cannot corrupt the run.
/// </summary>
public static class StepRunStateMachine
{
    private static readonly Dictionary<StepRunStatus, HashSet<StepRunStatus>> LegalTransitions = new()
    {
        [StepRunStatus.Pending] = new() { StepRunStatus.Ready, StepRunStatus.Running, StepRunStatus.Skipped, StepRunStatus.Cancelled },
        [StepRunStatus.Ready] = new() { StepRunStatus.Running, StepRunStatus.Skipped, StepRunStatus.Cancelled },
        [StepRunStatus.Running] = new()
        {
            StepRunStatus.WaitingDelay, StepRunStatus.WaitingApproval,
            StepRunStatus.Succeeded, StepRunStatus.Failed, StepRunStatus.Cancelled,
            StepRunStatus.OutcomeUnknown, StepRunStatus.BlockedPolicy
        },
        [StepRunStatus.WaitingDelay] = new() { StepRunStatus.Running, StepRunStatus.Skipped, StepRunStatus.Cancelled, StepRunStatus.BlockedPolicy },
        [StepRunStatus.WaitingApproval] = new() { StepRunStatus.Running, StepRunStatus.Skipped, StepRunStatus.Cancelled, StepRunStatus.BlockedPolicy },
        // Terminal-ish states: only explicit terminal markers are sinks.
        [StepRunStatus.Succeeded] = new(),
        [StepRunStatus.Skipped] = new(),
        [StepRunStatus.Failed] = new(),
        [StepRunStatus.Cancelled] = new(),
        [StepRunStatus.OutcomeUnknown] = new() { StepRunStatus.Succeeded, StepRunStatus.Failed, StepRunStatus.Cancelled, StepRunStatus.BlockedPolicy },
        [StepRunStatus.BlockedPolicy] = new()
    };

    /// <summary>True if <paramref name="to"/> is a legal successor of <paramref name="from"/>.</summary>
    public static bool CanTransition(StepRunStatus from, StepRunStatus to)
        => LegalTransitions.TryGetValue(from, out var outs) && outs.Contains(to);

    /// <summary>
    /// Validates a proposed step transition with a CAS guard on <paramref name="expectedRowVersion"/>.
    /// Returns a rejected <see cref="Result"/> (no mutation) on illegal transition or version mismatch.
    /// </summary>
    public static Result TryTransition(StepRunStatus current, int currentRowVersion, int expectedRowVersion,
        StepRunStatus proposed, out bool legal)
    {
        legal = CanTransition(current, proposed);
        if (!legal)
            return Result.Failure(RunErrors.StepStateRejectedError(current, proposed));
        if (currentRowVersion != expectedRowVersion)
            return Result.Failure(RunErrors.ConcurrencyConflictError());
        return Result.Success();
    }
}
