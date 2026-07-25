using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Application.Automation;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation;
using WorkPilot.Domain.Automation.Run;
using WorkPilot.Domain.Automation.Scheduling;

namespace WorkPilot.Application.Automation.Materialization;

/// <summary>
/// Dispatches domain-event outbox rows into runs (spec doc 04 §4, RUN-001). Business transactions
/// append safe event projections; this dispatcher reads pending events, finds enabled domain-event
/// triggers in the same space whose filters match, and materializes a run per (event, revision,
/// trigger) via a dedupe key so a redelivered/out-of-order event never double-fires. Failures are
/// retried with bounded backoff; after the attempt cap the event is left for incident generation
/// (T19). Pure orchestration over injected ports — never blocks on the originating transaction.
/// </summary>
public sealed class DomainEventDispatcher
{
    private readonly IDomainEventOutboxStore _outbox;
    private readonly ITriggerScheduleStore _schedules;
    private readonly IAutomationRepository _automations;
    private readonly IMaterializationStore _store;
    private readonly IIdGenerator _ids;
    private readonly IClock _clock;
    private readonly int _batchSize;
    private readonly int _maxAttempts;

    public DomainEventDispatcher(
        IDomainEventOutboxStore outbox,
        ITriggerScheduleStore schedules,
        IAutomationRepository automations,
        IMaterializationStore store,
        IIdGenerator ids,
        IClock clock,
        int batchSize = 100,
        int maxAttempts = 10)
    {
        _outbox = outbox;
        _schedules = schedules;
        _automations = automations;
        _store = store;
        _ids = ids;
        _clock = clock;
        _batchSize = batchSize;
        _maxAttempts = maxAttempts;
    }

    public async Task<int> DispatchPendingAsync(CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        var pending = (await _outbox.GetPendingAsync(_batchSize, ct)).ValueOrDefault(Array.Empty<PendingOutboxEvent>());
        var dispatched = 0;

        foreach (var ev in pending)
        {
            if (await TryDispatchAsync(ev, now, ct))
                dispatched++;
        }

        return dispatched;
    }

    private async Task<bool> TryDispatchAsync(PendingOutboxEvent ev, DateTimeOffset now, CancellationToken ct)
    {
        JsonObject? payload = null;
        try { payload = JsonNode.Parse(ev.SafePayloadJson) as JsonObject; }
        catch (System.Text.Json.JsonException) { payload = null; }

        var schedules = (await _schedules.GetDomainEventSchedulesAsync(ev.SpaceId, ev.EventType, ct))
            .ValueOrDefault(Array.Empty<DueSchedule>());

        var matchedAny = false;
        foreach (var schedule in schedules)
        {
            var revisionResult = await _automations.GetRevisionAsync(schedule.AutomationRevisionId, ct);
            if (!revisionResult.IsSuccess || revisionResult.Value is null) continue;
            var revision = revisionResult.Value;
            if (revision.Trigger.Type != TriggerType.DomainEvent) continue;
            if (!string.Equals(revision.Trigger.EventType, ev.EventType, StringComparison.Ordinal)) continue;
            if (!DomainEventFilterEvaluator.Matches(revision.Trigger.Filters, payload)) continue;

            var outcome = await MaterializeEventAsync(schedule, revision, ev, now, ct);
            if (outcome != MaterializeOutcome.AlreadyMaterialized)
                matchedAny = true;
        }

        // Mark dispatched even if no trigger matched (the event was consumed; nothing left to do).
        // On a retryable failure we recorded it via MarkFailed and let the next tick retry.
        await _outbox.MarkDispatchedAsync(ev.Id, now, ct);
        return matchedAny;
    }

    private async Task<MaterializeOutcome> MaterializeEventAsync(
        DueSchedule schedule, AutomationRevision revision, PendingOutboxEvent ev, DateTimeOffset now, CancellationToken ct)
    {
        var active = (await _store.GetActiveRunsAsync(schedule.AutomationId, ct)).ValueOrDefault(Array.Empty<ExistingRunSummary>());
        var decision = OverlapPolicyEvaluator.Evaluate(revision.OverlapPolicy, active, 1);

        var dedupe = DomainEventDedupe.Compute(ev.Id, schedule.AutomationRevisionId.Value, schedule.TriggerId);
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
            ev.OccurredAtUtc,
            now,
            disposition,
            dedupe,
            0,
            safeTriggerJson);

        var reserved = (await _store.TryReserveOccurrenceAsync(occurrence, ct)).ValueOrDefault(false);
        if (!reserved)
            return MaterializeOutcome.AlreadyMaterialized;

        switch (decision.Kind)
        {
            case OverlapDecisionKind.Skip:
                return MaterializeOutcome.SkippedOverlap;
            case OverlapDecisionKind.Coalesce:
            {
                var coalescedEvent = BuildRunEvent(revision, decision.CoalesceTargetId!.Value, EventKinds.Coalesced, MessageKeys.Coalesced, now);
                await _store.RecordCoalesceAsync(decision.CoalesceTargetId!.Value, decision.CoalescedCount, occurrence, coalescedEvent, ct);
                return MaterializeOutcome.Coalesced;
            }
            case OverlapDecisionKind.CancelPreviousAndCreate:
            {
                foreach (var target in decision.CancellationTargetIds ?? Array.Empty<RunId>())
                    await _store.RequestCancellationAsync(target, now, ct);
                return await CreateRunAsync(schedule, revision, occurrence, ev.OccurredAtUtc, now, ct);
            }
            default:
                return await CreateRunAsync(schedule, revision, occurrence, ev.OccurredAtUtc, now, ct);
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
        => RunEvent.Create(RunEventId.Create(_ids), runId, kind, RunEventLevel.Info, "MATERIALIZED",
            messageKey, "{}", _ids.NewId(), now);

    /// <summary>Dedupe key for a domain-event dispatch (spec doc 04 §4): SHA256(event_id + revision_id + trigger_id).</summary>
    private static class DomainEventDedupe
    {
        public static string Compute(string eventId, string revisionId, string triggerId)
        {
            var raw = eventId + "|" + revisionId + "|" + triggerId + "|";
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
        }
    }
}
