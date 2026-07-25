using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation.Run;
using Xunit;

namespace WorkPilot.Domain.Tests.Automation.Run;

public class RecoveryPlannerTests
{
    private static readonly SequentialIdGenerator Ids = new();
    private static readonly RunId Run = RunId.Parse("run_1");
    private static readonly StepRunId Step = StepRunId.Create(Ids);

    private static StepRun Stuck()
        => StepRun.Create(Step, Run, "cap_1", "capability_call", "idem_1", "digest_1", attempt: 2);

    [Fact]
    public void No_phase_is_safe_requeue()
        => Assert.Equal(RecoveryAction.Requeue, Plan(null, idem: false, verifiable: false, recovery: 0).Action);

    [Fact]
    public void Prepared_is_safe_requeue()
        => Assert.Equal(RecoveryAction.Requeue, Plan(SideEffectPhase.Prepared, false, false, 0).Action);

    [Fact]
    public void PermitIssued_is_safe_requeue()
        => Assert.Equal(RecoveryAction.Requeue, Plan(SideEffectPhase.PermitIssued, false, false, 0).Action);

    [Fact]
    public void RequestSending_with_idempotency_requeues_with_same_key()
    {
        var p = Plan(SideEffectPhase.RequestSending, idem: true, verifiable: false, recovery: 0);
        Assert.Equal(RecoveryAction.RequeueWithSameKey, p.Action);
        Assert.True(p.ReuseIdempotencyKey);
    }

    [Fact]
    public void RequestSending_without_idempotency_is_needs_review_never_auto_replay()
    {
        // Write outcome unknown and NOT idempotent -> human review, never auto-replay (T13 DoD).
        var p = Plan(SideEffectPhase.RequestSending, idem: false, verifiable: false, recovery: 0);
        Assert.Equal(RecoveryAction.NeedsReview, p.Action);
        Assert.Equal("RUN_RECOVERY_OUTCOME_UNKNOWN", p.ReasonCode);
    }

    [Fact]
    public void ResponseReceived_verifiable_completes_persist()
    {
        var p = Plan(SideEffectPhase.ResponseReceived, idem: true, verifiable: true, recovery: 0);
        Assert.Equal(RecoveryAction.CompletePersist, p.Action);
    }

    [Fact]
    public void ResponseReceived_unverifiable_is_needs_review()
    {
        var p = Plan(SideEffectPhase.ResponseReceived, idem: true, verifiable: false, recovery: 0);
        Assert.Equal(RecoveryAction.NeedsReview, p.Action);
    }

    [Fact]
    public void Recovery_count_over_limit_fails_repeated_crash()
    {
        var p = Plan(SideEffectPhase.RequestSending, idem: true, verifiable: false, recovery: Limits.V1_5.MaxApprovalRecoveryCount + 1);
        Assert.Equal(RecoveryAction.FailedRepeatedCrash, p.Action);
    }

    private static RecoveryPlan Plan(SideEffectPhase? lastPhase, bool idem, bool verifiable, int recovery)
        => RecoveryPlanner.Plan(Stuck(), lastPhase, idem, verifiable, recovery);
}
