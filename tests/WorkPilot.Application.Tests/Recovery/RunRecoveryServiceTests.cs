using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation;
using WorkPilot.Domain.Automation.Run;
using WorkPilot.Application.Automation.Run.Permit;
using WorkPilot.Application.Automation.Run.Recovery;
using Xunit;

namespace WorkPilot.Application.Tests.Recovery;

public class RunRecoveryServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    private static readonly SequentialIdGenerator Ids = new();
    private static readonly RunId RunId = RunId.Parse("run_1");

    private static AutomationRun RunningRun(int recoveryCount = 0)
        => (AutomationRun.Create(RunId, AutomationRevisionId.Parse("rev_1"), RunSnapshotId.Parse("snap_1"),
                RunTriggerKind.Interval, Now, Now).MarkClaimed("w", Now.AddMinutes(1), Now).MarkRunning(Now))
            with { RecoveryCount = recoveryCount };

    private static StepRun RunningStep(string nodeId = "cap_1")
        => StepRun.Create(StepRunId.Create(Ids), RunId, nodeId, "capability_call", "idem_1", "digest_1", attempt: 1)
            with { Status = StepRunStatus.Running };

    private static RunRecoveryService Svc(InMemorySideEffectJournal journal, bool idempotent)
        => new(journal, new FakeIdem(idempotent), new FakeClock(Now), Ids);

    [Fact]
    public void Prepared_phase_is_safe_requeue()
    {
        var run = RunningRun();
        var step = RunningStep();
        var journal = new InMemorySideEffectJournal();
        journal.Record(new SideEffectPhaseRecord(run.Id.Value, step.Id.Value, SideEffectPhase.Prepared, Now));

        var result = Svc(journal, idempotent: false).Recover(run, new[] { step });

        Assert.True(result.ActionTaken);
        Assert.Equal(RunStatus.Queued, result.Run.Status);
        Assert.Equal(1, result.Run.RecoveryCount);
        Assert.Equal(StepRunStatus.Failed, result.Steps.Single().Status);
    }

    [Fact]
    public void RequestSending_with_idempotency_requeues_with_same_key()
    {
        var run = RunningRun();
        var step = RunningStep();
        var journal = new InMemorySideEffectJournal();
        journal.Record(new SideEffectPhaseRecord(run.Id.Value, step.Id.Value, SideEffectPhase.RequestSending, Now));

        var result = Svc(journal, idempotent: true).Recover(run, new[] { step });

        Assert.Equal(RunStatus.Queued, result.Run.Status);
        Assert.Equal(StepRunStatus.Failed, result.Steps.Single().Status);
        Assert.Contains(result.Events, e => e.Code == RunEventCodes.RecoveryIdempotentRequeue);
    }

    [Fact]
    public void RequestSending_without_idempotency_is_needs_review_never_auto_replay()
    {
        // Unknown write outcome, NOT idempotent -> NeedsReview, run must NOT be replayed.
        var run = RunningRun();
        var step = RunningStep();
        var journal = new InMemorySideEffectJournal();
        journal.Record(new SideEffectPhaseRecord(run.Id.Value, step.Id.Value, SideEffectPhase.RequestSending, Now));

        var result = Svc(journal, idempotent: false).Recover(run, new[] { step });

        Assert.Equal(RunStatus.NeedsReview, result.Run.Status);
        Assert.Equal(StepRunStatus.OutcomeUnknown, result.Steps.Single().Status);
        Assert.Contains(result.Events, e => e.Code == RunEventCodes.RecoveryNeedsReview);
    }

    [Fact]
    public void ResponseReceived_verifiable_completes_persist()
    {
        var run = RunningRun();
        var step = RunningStep();
        var journal = new InMemorySideEffectJournal();
        journal.Record(new SideEffectPhaseRecord(run.Id.Value, step.Id.Value, SideEffectPhase.ResponseReceived, Now, "safe_summary"));

        var result = Svc(journal, idempotent: true).Recover(run, new[] { step });

        Assert.Equal(StepRunStatus.Succeeded, result.Steps.Single().Status);
        Assert.Equal("safe_summary", result.Steps.Single().OutputSummaryJson);
        Assert.Equal(RunStatus.Queued, result.Run.Status);
    }

    [Fact]
    public void ResponseReceived_unverifiable_is_needs_review()
    {
        var run = RunningRun();
        var step = RunningStep();
        var journal = new InMemorySideEffectJournal();
        journal.Record(new SideEffectPhaseRecord(run.Id.Value, step.Id.Value, SideEffectPhase.ResponseReceived, Now, null));

        var result = Svc(journal, idempotent: true).Recover(run, new[] { step });

        Assert.Equal(RunStatus.NeedsReview, result.Run.Status);
    }

    [Fact]
    public void Recovery_count_over_limit_fails_repeated_crash()
    {
        var run = RunningRun(recoveryCount: Limits.V1_5.MaxApprovalRecoveryCount + 1);
        var step = RunningStep();
        var journal = new InMemorySideEffectJournal();
        journal.Record(new SideEffectPhaseRecord(run.Id.Value, step.Id.Value, SideEffectPhase.RequestSending, Now));

        var result = Svc(journal, idempotent: true).Recover(run, new[] { step });

        Assert.Equal(RunStatus.Failed, result.Run.Status);
        Assert.Equal("RUN_RECOVERY_REPEATED_CRASH", result.Run.FinalErrorCode);
        Assert.Contains(result.Events, e => e.Level == RunEventLevel.Security);
    }

    [Fact]
    public void No_stuck_step_is_no_op()
    {
        var run = RunningRun();
        var step = RunningStep() with { Status = StepRunStatus.Succeeded };
        var journal = new InMemorySideEffectJournal();

        var result = Svc(journal, idempotent: false).Recover(run, new[] { step });

        Assert.False(result.ActionTaken);
        Assert.Equal(RunStatus.Running, result.Run.Status);
    }

    private sealed class FakeIdem(bool supports) : IProviderIdempotencyResolver
    {
        public bool SupportsIdempotency(StepRun step) => supports;
    }
}
