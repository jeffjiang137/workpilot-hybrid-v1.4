using WorkPilot.Domain.Automation.Run;
using WorkPilot.Domain.Automation.Run.Interpreter;
using Xunit;

namespace WorkPilot.Domain.Tests.Automation.Run;

/// <summary>Run/Step lifecycle transition tables (doc 03 §7.1/§7.2). Verifies every legal edge and
/// that illegal edges + version mismatches are rejected without mutation.</summary>
public class RunStateMachineTests
{
    // ---- Run state machine (doc 03 §7.1) ----

    [Theory]
    [InlineData(RunStatus.Queued, RunStatus.Claimed)]
    [InlineData(RunStatus.Queued, RunStatus.Running)]
    [InlineData(RunStatus.Queued, RunStatus.Cancelled)]
    [InlineData(RunStatus.Queued, RunStatus.BlockedPolicy)]
    [InlineData(RunStatus.Claimed, RunStatus.Running)]
    [InlineData(RunStatus.Claimed, RunStatus.Queued)]      // lease expired before side effect
    [InlineData(RunStatus.Claimed, RunStatus.NeedsReview)] // lease lost during unknown write window
    [InlineData(RunStatus.Running, RunStatus.WaitingDelay)]
    [InlineData(RunStatus.Running, RunStatus.WaitingApproval)]
    [InlineData(RunStatus.Running, RunStatus.Completed)]
    [InlineData(RunStatus.Running, RunStatus.Failed)]
    [InlineData(RunStatus.Running, RunStatus.NeedsReview)]
    [InlineData(RunStatus.WaitingDelay, RunStatus.Queued)]
    [InlineData(RunStatus.WaitingApproval, RunStatus.Queued)]
    public void Run_legal_transitions_are_allowed(RunStatus from, RunStatus to)
    {
        Assert.True(RunStateMachine.CanTransition(from, to));
        var r = RunStateMachine.TryTransition(from, 3, 3, to, out var legal);
        Assert.True(legal);
        Assert.True(r.IsSuccess);
    }

    [Theory]
    [InlineData(RunStatus.Queued, RunStatus.Completed)]
    [InlineData(RunStatus.Completed, RunStatus.Running)]     // terminal has no outbound
    [InlineData(RunStatus.Failed, RunStatus.Queued)]
    [InlineData(RunStatus.Cancelled, RunStatus.Running)]
    [InlineData(RunStatus.WaitingDelay, RunStatus.Completed)]
    [InlineData(RunStatus.Running, RunStatus.Claimed)]       // no going back to claimed
    public void Run_illegal_transitions_are_rejected(RunStatus from, RunStatus to)
    {
        Assert.False(RunStateMachine.CanTransition(from, to));
        var r = RunStateMachine.TryTransition(from, 1, 1, to, out var legal);
        Assert.False(legal);
        Assert.False(r.IsSuccess);
        Assert.Equal("RUN_STATE_REJECTED", r.Error!.Code);
    }

    [Fact]
    public void Run_version_mismatch_is_a_concurrency_conflict()
    {
        var r = RunStateMachine.TryTransition(RunStatus.Running, currentRowVersion: 5, expectedRowVersion: 4,
            RunStatus.Completed, out var legal);
        Assert.True(legal); // transition itself is legal
        Assert.False(r.IsSuccess);
        Assert.Equal("RUN_CONCURRENCY", r.Error!.Code);
    }

    // ---- Step state machine (doc 03 §7.2) ----

    [Theory]
    [InlineData(StepRunStatus.Pending, StepRunStatus.Ready)]
    [InlineData(StepRunStatus.Pending, StepRunStatus.Running)]
    [InlineData(StepRunStatus.Pending, StepRunStatus.Skipped)]
    [InlineData(StepRunStatus.Pending, StepRunStatus.Cancelled)]
    [InlineData(StepRunStatus.Ready, StepRunStatus.Running)]
    [InlineData(StepRunStatus.Running, StepRunStatus.Succeeded)]
    [InlineData(StepRunStatus.Running, StepRunStatus.Failed)]
    [InlineData(StepRunStatus.Running, StepRunStatus.WaitingDelay)]
    [InlineData(StepRunStatus.Running, StepRunStatus.WaitingApproval)]
    [InlineData(StepRunStatus.Running, StepRunStatus.OutcomeUnknown)]
    [InlineData(StepRunStatus.Running, StepRunStatus.BlockedPolicy)]
    [InlineData(StepRunStatus.WaitingDelay, StepRunStatus.Running)]
    [InlineData(StepRunStatus.WaitingApproval, StepRunStatus.Running)]
    [InlineData(StepRunStatus.OutcomeUnknown, StepRunStatus.Succeeded)]
    [InlineData(StepRunStatus.OutcomeUnknown, StepRunStatus.Failed)]
    public void Step_legal_transitions_are_allowed(StepRunStatus from, StepRunStatus to)
    {
        Assert.True(StepRunStateMachine.CanTransition(from, to));
        var r = StepRunStateMachine.TryTransition(from, 2, 2, to, out var legal);
        Assert.True(legal);
        Assert.True(r.IsSuccess);
    }

    [Theory]
    [InlineData(StepRunStatus.Pending, StepRunStatus.Succeeded)]  // must go via Running
    [InlineData(StepRunStatus.Pending, StepRunStatus.Failed)]
    [InlineData(StepRunStatus.Succeeded, StepRunStatus.Failed)]   // terminal sink
    [InlineData(StepRunStatus.Skipped, StepRunStatus.Running)]
    [InlineData(StepRunStatus.Cancelled, StepRunStatus.Succeeded)]
    public void Step_illegal_transitions_are_rejected(StepRunStatus from, StepRunStatus to)
    {
        Assert.False(StepRunStateMachine.CanTransition(from, to));
        var r = StepRunStateMachine.TryTransition(from, 1, 1, to, out var legal);
        Assert.False(legal);
        Assert.False(r.IsSuccess);
        Assert.Equal("RUN_STEP_STATE_REJECTED", r.Error!.Code);
    }

    [Fact]
    public void Step_version_mismatch_is_a_concurrency_conflict()
    {
        var r = StepRunStateMachine.TryTransition(StepRunStatus.Running, 7, 6, StepRunStatus.Succeeded, out var legal);
        Assert.True(legal);
        Assert.False(r.IsSuccess);
        Assert.Equal("RUN_CONCURRENCY", r.Error!.Code);
    }
}
