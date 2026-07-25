using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation;
using WorkPilot.Domain.Automation.Run;
using WorkPilot.Application.Automation.Run.Permit;

namespace WorkPilot.Application.Automation.Run.Recovery;

/// <summary>Result of a recovery pass: the mutated run, updated steps, and emitted events.</summary>
public sealed record RecoveryResult(
    AutomationRun Run,
    IReadOnlyList<StepRun> Steps,
    IReadOnlyList<RunEvent> Events,
    bool ActionTaken);

/// <summary>
/// Applies the doc 04 §9 / §13 crash-recovery decision to a run whose worker died. It reads the
/// side-effect journal for the stuck step, asks <see cref="IProviderIdempotencyResolver"/> whether the
/// provider is idempotent, and maps the <see cref="RecoveryPlanner"/> decision onto run/step transitions.
/// Critically, a write with an UNKNOWN outcome that is NOT idempotent is routed to
/// <see cref="RunStatus.NeedsReview"/> and is NEVER auto-replayed (T13 DoD). All recovery actions emit
/// <see cref="RunEvent"/>s; the caller persists the returned objects.
/// </summary>
public sealed class RunRecoveryService
{
    private readonly ISideEffectJournal _journal;
    private readonly IProviderIdempotencyResolver _idempotency;
    private readonly IClock _clock;
    private readonly IIdGenerator _ids;

    public RunRecoveryService(
        ISideEffectJournal journal,
        IProviderIdempotencyResolver idempotency,
        IClock clock,
        IIdGenerator ids)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _idempotency = idempotency ?? throw new ArgumentNullException(nameof(idempotency));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
    }

    public RecoveryResult Recover(AutomationRun run, IReadOnlyList<StepRun> steps)
    {
        var stuck = FindStuckStep(run, steps);
        if (stuck is null)
            return new RecoveryResult(run, steps, Array.Empty<RunEvent>(), false);

        var now = _clock.UtcNow;
        var entries = _journal.EntriesFor(run.Id.Value, stuck.Id.Value);
        var lastPhase = SideEffectJournalReader.LastPhase(entries);
        var isWriteEffect = lastPhase is not null; // a phase was recorded => capability write step
        var providerSupportsIdempotency = isWriteEffect && _idempotency.SupportsIdempotency(stuck);
        var responseVerifiable = entries.Any(e =>
            e.Phase == SideEffectPhase.ResponseReceived && !string.IsNullOrEmpty(e.Detail));

        var plan = RecoveryPlanner.Plan(stuck, lastPhase, providerSupportsIdempotency, responseVerifiable, run.RecoveryCount);
        return Apply(run, steps, stuck, plan, entries, now);
    }

    private RecoveryResult Apply(
        AutomationRun run, IReadOnlyList<StepRun> steps, StepRun stuck,
        RecoveryPlan plan, IReadOnlyList<SideEffectPhaseRecord> entries, DateTimeOffset now)
    {
        StepRun newStep = stuck;
        AutomationRun newRun = run;
        RunEvent? evt = null;

        switch (plan.Action)
        {
            case RecoveryAction.Requeue:
            case RecoveryAction.RequeueWithSameKey:
                // Mark this attempt failed and re-queue the run for re-claim (idempotency key preserved).
                newStep = stuck with
                {
                    Status = StepRunStatus.Failed,
                    ErrorCode = plan.Action == RecoveryAction.RequeueWithSameKey
                        ? RunEventCodes.RecoveryIdempotentRequeue : RunEventCodes.RecoveryRequeued,
                    FinishedAtUtc = now,
                    RowVersion = stuck.RowVersion + 1
                };
                newRun = run.RecoverToQueued(now);
                evt = MakeEvent(run.Id, stuck.Id, RunEventLevel.Warning,
                    plan.Action == RecoveryAction.RequeueWithSameKey
                        ? RunEventCodes.RecoveryIdempotentRequeue : RunEventCodes.RecoveryRequeued,
                    new Dictionary<string, string> { ["step_id"] = stuck.Id.Value, ["reuse_key"] = plan.ReuseIdempotencyKey.ToString() });
                break;

            case RecoveryAction.NeedsReview:
                // Write outcome unknown (or unverifiable) and NOT idempotent => human review, no replay.
                newStep = stuck with
                {
                    Status = StepRunStatus.OutcomeUnknown,
                    ErrorCode = RunErrors.RecoveryOutcomeUnknownError(stuck.NodeId).Code,
                    FinishedAtUtc = now,
                    RowVersion = stuck.RowVersion + 1
                };
                newRun = run.ExpireToNeedsReview(now);
                evt = MakeEvent(run.Id, stuck.Id, RunEventLevel.Error, RunEventCodes.RecoveryNeedsReview,
                    new Dictionary<string, string> { ["step_id"] = stuck.Id.Value, ["reason"] = plan.ReasonCode });
                break;

            case RecoveryAction.CompletePersist:
                // Response safely recorded; finish persistence and continue.
                var detail = entries.LastOrDefault(e => e.Phase == SideEffectPhase.ResponseReceived)?.Detail;
                newStep = stuck with
                {
                    Status = StepRunStatus.Succeeded,
                    OutputSummaryJson = detail,
                    FinishedAtUtc = now,
                    RowVersion = stuck.RowVersion + 1
                };
                newRun = run.RecoverToQueued(now);
                evt = MakeEvent(run.Id, stuck.Id, RunEventLevel.Info, RunEventCodes.RecoveryCompleted,
                    new Dictionary<string, string> { ["step_id"] = stuck.Id.Value });
                break;

            case RecoveryAction.FailedRepeatedCrash:
                newStep = stuck with
                {
                    Status = StepRunStatus.Failed,
                    ErrorCode = RunErrors.RecoveryRepeatedCrashError(run.Id.Value).Code,
                    FinishedAtUtc = now,
                    RowVersion = stuck.RowVersion + 1
                };
                newRun = run.MarkFailed(now, RunErrors.RecoveryRepeatedCrashError(run.Id.Value).Code);
                evt = MakeEvent(run.Id, stuck.Id, RunEventLevel.Security, RunEventCodes.RecoveryRepeatedCrash,
                    new Dictionary<string, string> { ["step_id"] = stuck.Id.Value, ["run_id"] = run.Id.Value });
                break;
        }

        var newSteps = steps.Select(s => s.Id == stuck.Id ? newStep : s).ToArray();
        var events = evt is null ? Array.Empty<RunEvent>() : new[] { evt };
        return new RecoveryResult(newRun, newSteps, events, true);
    }

    private static StepRun? FindStuckStep(AutomationRun run, IReadOnlyList<StepRun> steps)
    {
        foreach (var s in steps)
            if (s.Status == StepRunStatus.Running && (run.CurrentNodeId is null || s.NodeId == run.CurrentNodeId))
                return s;
        foreach (var s in steps)
            if (s.Status == StepRunStatus.Running)
                return s;
        return null;
    }

    private RunEvent MakeEvent(RunId runId, StepRunId stepId, RunEventLevel level, string code, Dictionary<string, string> props)
    {
        var json = JsonSerializer.Serialize(props);
        return RunEvent.Create(RunEventId.Create(_ids), runId, RunEventKinds.Recovery, level, code, code,
            json, runId.Value, _clock.UtcNow, stepId, null);
    }
}
