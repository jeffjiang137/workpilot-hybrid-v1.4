using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Application.Automation;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation;
using WorkPilot.Domain.Automation.Run;
using WorkPilot.Domain.Automation.Run.Materialization;
using WorkPilot.Domain.Automation.Scheduling;

namespace WorkPilot.Application.Automation.Materialization;

/// <summary>
/// Materializes scheduled (interval/calendar) triggers into queued runs (spec doc 04 §2/§3,
/// RUN-001/009/010). It is the single writer of new runs: for each due schedule it enumerates the
/// occurrences that should have fired in (lastMaterialized, now] using the shared
/// <see cref="MissedRunResolver"/> (so missed-run policy is identical to live scheduling), applies the
/// revision's <see cref="OverlapPolicy"/> via <see cref="OverlapPolicyEvaluator"/>, and persists an
/// idempotent occurrence (dedupe key) plus the run. Pointers are always advanced so a crash mid-batch
/// resumes exactly where it left off. Pure orchestration — all I/O goes through injected ports.
/// </summary>
public sealed class TriggerMaterializer
{
    private readonly ITriggerScheduleStore _schedules;
    private readonly IAutomationRepository _automations;
    private readonly IMaterializationStore _store;
    private readonly IIdGenerator _ids;
    private readonly IClock _clock;
    private readonly ITimeZoneResolver _tz;
    private readonly int _batchSize;

    public TriggerMaterializer(
        ITriggerScheduleStore schedules,
        IAutomationRepository automations,
        IMaterializationStore store,
        IIdGenerator ids,
        IClock clock,
        ITimeZoneResolver tz,
        int batchSize = 100)
    {
        _schedules = schedules;
        _automations = automations;
        _store = store;
        _ids = ids;
        _clock = clock;
        _tz = tz;
        _batchSize = batchSize;
    }

    public TriggerMaterializer(
        ITriggerScheduleStore schedules,
        IAutomationRepository automations,
        IMaterializationStore store,
        IIdGenerator ids,
        IClock clock,
        ITimeZoneResolver tz)
        : this(schedules, automations, store, ids, clock, tz, 100) { }

    public async Task<MaterializationBatchResult> MaterializeDueAsync(CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        var due = (await _schedules.GetDueSchedulesAsync(now, _batchSize, ct)).ValueOrDefault(Array.Empty<DueSchedule>());
        var created = 0;
        var skipped = 0;

        foreach (var schedule in due)
        {
            var result = await MaterializeScheduleAsync(schedule, now, ct);
            created += result.RunsCreated;
            skipped += result.OccurrencesSkipped;
        }

        return new MaterializationBatchResult(due.Count, created, skipped);
    }

    private async Task<MaterializationBatchResult> MaterializeScheduleAsync(DueSchedule schedule, DateTimeOffset now, CancellationToken ct)
    {
        // Domain-event / manual triggers are not time-based here; the outbox dispatcher handles them.
        var revisionResult = await _automations.GetRevisionAsync(schedule.AutomationRevisionId, ct);
        if (!revisionResult.IsSuccess || revisionResult.Value is null)
            return new MaterializationBatchResult(0, 0, 0);

        var revision = revisionResult.Value;
        if (revision.Trigger.Type is TriggerType.Manual or TriggerType.DomainEvent)
            return new MaterializationBatchResult(0, 0, 0);

        var from = schedule.LastMaterializedAtUtc
                   ?? revision.Trigger.AnchorAtUtc
                   ?? DateTimeOffset.MinValue;
        var missed = MissedRunResolver.Resolve(revision.Trigger, from, now, revision.MissedRunPolicy, _tz);

        var created = 0;
        var skipped = 0;
        foreach (var candidate in missed.Occurrences)
        {
            var outcome = await MaterializeCandidateAsync(schedule, revision, candidate, now, ct);
            if (outcome is MaterializeOutcome.Created or MaterializeOutcome.Coalesced)
                created++;
            else if (outcome is MaterializeOutcome.SkippedOverlap or MaterializeOutcome.Blocked)
                skipped++;
        }

        var newLast = missed.LastCandidateUtc ?? now;
        var next = ScheduleCalculator.ComputeNext(revision.Trigger, newLast, _tz);
        var nextUtc = next.HasOccurrence ? next.Occurrence!.Utc : (DateTimeOffset?)null;
        await _schedules.UpdatePointerAsync(schedule.AutomationId, schedule.AutomationRevisionId,
            schedule.TriggerId, newLast, nextUtc, ct);

        return new MaterializationBatchResult(1, created, skipped);
    }

    private async Task<MaterializeOutcome> MaterializeCandidateAsync(
        DueSchedule schedule, AutomationRevision revision, DateTimeOffset scheduledAtUtc, DateTimeOffset now, CancellationToken ct)
    {
        var active = (await _store.GetActiveRunsAsync(schedule.AutomationId, ct)).ValueOrDefault(Array.Empty<ExistingRunSummary>());
        var decision = OverlapPolicyEvaluator.Evaluate(revision.OverlapPolicy, active, 1);

        var dedupe = TriggerOccurrenceDedupe.Compute(schedule.AutomationId, schedule.AutomationRevisionId,
            schedule.TriggerId, scheduledAtUtc);
        var safeTriggerJson = revision.Trigger.ToCanonicalJson().ToJsonString();

        var disposition = decision.Kind switch
        {
            OverlapDecisionKind.Skip => OccurrenceDisposition.SkippedOverlap,
            OverlapDecisionKind.Coalesce => OccurrenceDisposition.Coalesced,
            _ => OccurrenceDisposition.Queued
        };

        var occurrence = TriggerOccurrence.Create(
            TriggerOccurrenceId.Create(_ids),
            schedule.AutomationId,
            schedule.AutomationRevisionId,
            schedule.TriggerId,
            scheduledAtUtc,
            now,
            disposition,
            dedupe,
            0,
            safeTriggerJson);

        var reserved = (await _store.TryReserveOccurrenceAsync(occurrence, ct)).ValueOrDefault(false);
        if (!reserved)
            return MaterializeOutcome.AlreadyMaterialized; // idempotent: another worker/host already did it

        switch (decision.Kind)
        {
            case OverlapDecisionKind.Skip:
                return MaterializeOutcome.SkippedOverlap;

            case OverlapDecisionKind.Coalesce:
            {
                var coalescedEvent = BuildRunEvent(revision, decision.CoalesceTargetId!.Value, EventKinds.Coalesced,
                    MessageKeys.Coalesced, now);
                await _store.RecordCoalesceAsync(decision.CoalesceTargetId!.Value, decision.CoalescedCount,
                    occurrence, coalescedEvent, ct);
                return MaterializeOutcome.Coalesced;
            }

            case OverlapDecisionKind.CancelPreviousAndCreate:
            {
                foreach (var target in decision.CancellationTargetIds ?? Array.Empty<RunId>())
                    await _store.RequestCancellationAsync(target, now, ct);
                return await CreateRunAsync(schedule, revision, occurrence, scheduledAtUtc, now, ct);
            }

            default: // Create
                return await CreateRunAsync(schedule, revision, occurrence, scheduledAtUtc, now, ct);
        }
    }

    private async Task<MaterializeOutcome> CreateRunAsync(
        DueSchedule schedule, AutomationRevision revision, TriggerOccurrence occurrence,
        DateTimeOffset scheduledAtUtc, DateTimeOffset now, CancellationToken ct)
    {
        var expertRevisionId = revision.Binding.ExpertId is { } e
            ? ExpertRevisionId.Parse(e)
            : ExpertRevisionId.Parse("unbound");

        var snapshot = RunSnapshotFactory.Build(_ids, revision, expertRevisionId, revocationEpoch: 0, now);
        var run = AutomationRun.Create(
            RunId.Create(_ids),
            schedule.AutomationRevisionId,
            snapshot.Id,
            TriggerKindMapper.ToRunTriggerKind(revision.Trigger.Type),
            scheduledAtUtc,
            scheduledAtUtc,
            automationId: schedule.AutomationId,
            occurrenceId: occurrence.Id,
            priority: 0);

        var createdEvent = BuildRunEvent(revision, run.Id, EventKinds.RunCreated, MessageKeys.RunCreated, now);
        await _store.CreateRunForOccurrenceAsync(run, snapshot, createdEvent, ct);
        return MaterializeOutcome.Created;
    }

    private RunEvent BuildRunEvent(AutomationRevision revision, RunId runId, string kind, string messageKey, DateTimeOffset now)
        => RunEvent.Create(
            RunEventId.Create(_ids),
            runId,
            kind,
            RunEventLevel.Info,
            "MATERIALIZED",
            messageKey,
            "{}",
            _ids.NewId(),
            now);
}
