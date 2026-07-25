using System.Collections.Generic;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;

namespace WorkPilot.Domain.Automation.Run;

/// <summary>What crash recovery should do with a stuck side-effect step (doc 04 §9 / §13).</summary>
public enum RecoveryAction
{
    /// <summary>No point of no return reached: safe to requeue and retry (prepared / permit_issued).</summary>
    Requeue,

    /// <summary>Sent with a provider idempotency key but no response: retry reusing the SAME key (doc 04 §9).</summary>
    RequeueWithSameKey,

    /// <summary>Write outcome unknown and NOT idempotent: must NOT auto-replay. Human review (doc 04 §9).</summary>
    NeedsReview,

    /// <summary>Response received but not persisted: finish persistence if verifiable, else review.</summary>
    CompletePersist,

    /// <summary>Too many recoveries: fail the run as a repeated worker crash (High Incident, doc 04 §13).</summary>
    FailedRepeatedCrash
}

/// <summary>Pure crash-recovery decision for one step (doc 04 §9 / §13). No I/O.</summary>
public sealed record RecoveryPlan(
    RecoveryAction Action,
    string ReasonCode,
    StepRunId StepId,
    bool ReuseIdempotencyKey);

/// <summary>
/// Decides the recovery action for a side-effect step from its last recorded journal phase (doc 04 §9):
/// <list type="bullet">
///   <item>no phase / <see cref="SideEffectPhase.Prepared"/> / <see cref="SideEffectPhase.PermitIssued"/> → safe <see cref="RecoveryAction.Requeue"/>;</item>
///   <item><see cref="SideEffectPhase.RequestSending"/> with provider idempotency → <see cref="RecoveryAction.RequeueWithSameKey"/>;</item>
///   <item><see cref="SideEffectPhase.RequestSending"/> without idempotency → <see cref="RecoveryAction.NeedsReview"/> (never auto-replay);</item>
///   <item><see cref="SideEffectPhase.ResponseReceived"/> verifiable → <see cref="RecoveryAction.CompletePersist"/>, else <see cref="RecoveryAction.NeedsReview"/>;</item>
///   <item>recovery count &gt; <see cref="Limits.V1_5.MaxApprovalRecoveryCount"/> → <see cref="RecoveryAction.FailedRepeatedCrash"/>.</item>
/// </list>
/// </summary>
public static class RecoveryPlanner
{
    public static RecoveryPlan Plan(
        StepRun step,
        SideEffectPhase? lastPhase,
        bool providerSupportsIdempotency,
        bool responseVerifiable,
        int recoveryCount)
    {
        if (recoveryCount > Limits.V1_5.MaxApprovalRecoveryCount)
            return new RecoveryPlan(RecoveryAction.FailedRepeatedCrash, "RUN_RECOVERY_REPEATED_CRASH", step.Id, false);

        // No side effect started, or before the point of no return → safe requeue.
        if (lastPhase is null or SideEffectPhase.Prepared or SideEffectPhase.PermitIssued)
            return new RecoveryPlan(RecoveryAction.Requeue, "RUN_RECOVERY_SAFE_RETRY", step.Id, false);

        if (lastPhase == SideEffectPhase.RequestSending)
        {
            // Sent but no response observed.
            if (providerSupportsIdempotency)
                return new RecoveryPlan(RecoveryAction.RequeueWithSameKey, "RUN_RECOVERY_IDEMPOTENT_RETRY", step.Id, true);
            // Write outcome unknown and not idempotent → MUST NOT auto-replay.
            return new RecoveryPlan(RecoveryAction.NeedsReview, "RUN_RECOVERY_OUTCOME_UNKNOWN", step.Id, false);
        }

        if (lastPhase == SideEffectPhase.ResponseReceived)
        {
            // Adapter wrote a safe summary to the journal; complete persistence if verifiable.
            if (responseVerifiable)
                return new RecoveryPlan(RecoveryAction.CompletePersist, "RUN_RECOVERY_COMPLETE_PERSIST", step.Id, false);
            return new RecoveryPlan(RecoveryAction.NeedsReview, "RUN_RECOVERY_UNVERIFIABLE", step.Id, false);
        }

        // Persisted: nothing to redo.
        return new RecoveryPlan(RecoveryAction.Requeue, "RUN_RECOVERY_SAFE_RETRY", step.Id, false);
    }
}
