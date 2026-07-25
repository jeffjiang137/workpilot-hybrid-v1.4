using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using WorkPilot.Application.Automation;
using WorkPilot.Application.Automation.Materialization;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation;
using WorkPilot.Domain.Automation.Run;
using WorkPilot.Domain.Automation.Scheduling;
using WorkPilot.Infrastructure.Automation;
using WorkPilot.Infrastructure.Automation.Materialization;
using Xunit;

namespace WorkPilot.Infrastructure.Tests;

/// <summary>RUN-001/002/009/010 + Host-crash recovery: full scheduled materialization → claim → recover loop against real SQLite.</summary>
public class MaterializationEndToEndTests
{
    private const string Auto = "auto_1";
    private const string Rev = "rev_1";
    private static readonly DateTimeOffset T0 = MaterializationTestKit.Now;
    private static readonly DateTimeOffset T3 = T0.AddHours(3); // materializer "now", past the anchor

    [Fact]
    public async Task ScheduledMaterialization_materializes_claims_and_recovers_on_crash()
    {
        var conn = await MaterializationTestKit.OpenAsync(":memory:");
        await using var _ = conn;
        await MaterializationTestKit.EnsureSchemaAsync(conn);
        await MaterializationTestKit.SeedRevisionAsync(conn, AutomationId.Parse(Auto), AutomationRevisionId.Parse(Rev));

        var schedules = new TriggerScheduleRepository(conn);
        await schedules.UpsertAsync(AutomationId.Parse(Auto), AutomationRevisionId.Parse(Rev),
            MaterializationTestKit.IntervalTrigger(), T0.AddHours(1), T0, CancellationToken.None);

        var store = new RunRepository(conn);
        var automations = new AutomationRepository(conn);
        var clock = new MutableClock { UtcNow = T3 };
        var ids = new SequentialIdGenerator();

        var materializer = new TriggerMaterializer(schedules, automations, store, ids, clock, new UtcResolver());
        var claims = new RunClaimService(store, ids, clock, "worker_a", TimeSpan.FromSeconds(30), globalSlots: 4);

        // 1) Materialize due occurrences (RunOnce => a single run at T3).
        var batch = await materializer.MaterializeDueAsync(CancellationToken.None);
        Assert.Equal(1, batch.RunsCreated);
        Assert.Equal(1L, await MaterializationTestKit.CountAsync(conn, "SELECT COUNT(*) FROM automation_runs"));

        // 2) Claim the queued run; it must become claimed with a lease owned by worker_a.
        var claim = await claims.ClaimAvailableAsync(CancellationToken.None);
        Assert.Equal(1, claim.Claimed);
        var claimedId = claim.ClaimedRunIds[0];
        var got = (await store.GetRunAsync(claimedId, CancellationToken.None)).Value!;
        Assert.Equal(RunStatus.Claimed, got.Run.Status);
        Assert.Equal("worker_a", got.Run.LeaseOwner);

        // 3) Simulate a Host crash: advance past the lease and recover. The run must be requeued
        //    (never double-executed) and ready for another worker.
        clock.UtcNow = T3.AddMinutes(1);
        var recovered = await claims.RecoverExpiredAsync(CancellationToken.None);
        Assert.Single(recovered);
        var after = (await store.GetRunAsync(recovered[0], CancellationToken.None)).Value!;
        Assert.Equal(RunStatus.Queued, after.Run.Status);
        Assert.Null(after.Run.LeaseOwner);
    }

    [Fact]
    public async Task ScheduledMaterialization_is_idempotent_across_ticks_RUN_009_010()
    {
        var conn = await MaterializationTestKit.OpenAsync(":memory:");
        await using var _ = conn;
        await MaterializationTestKit.EnsureSchemaAsync(conn);
        await MaterializationTestKit.SeedRevisionAsync(conn, AutomationId.Parse(Auto), AutomationRevisionId.Parse(Rev));

        var schedules = new TriggerScheduleRepository(conn);
        await schedules.UpsertAsync(AutomationId.Parse(Auto), AutomationRevisionId.Parse(Rev),
            MaterializationTestKit.IntervalTrigger(), T0.AddHours(1), T0, CancellationToken.None);

        var store = new RunRepository(conn);
        var automations = new AutomationRepository(conn);
        var clock = new MutableClock { UtcNow = T3 };
        var ids = new SequentialIdGenerator();
        var materializer = new TriggerMaterializer(schedules, automations, store, ids, clock, new UtcResolver());

        Assert.Equal(1, (await materializer.MaterializeDueAsync(CancellationToken.None)).RunsCreated);
        Assert.Equal(1L, await MaterializationTestKit.CountAsync(conn, "SELECT COUNT(*) FROM automation_runs"));

        // Replay the same window: reset the schedule pointer + due hint, re-run at the same now.
        // The candidate instant is identical, so the dedupe UNIQUE key rejects the duplicate → no new run.
        await MaterializationTestKit.ResetScheduleAsync(conn, Rev);
        Assert.Equal(0, (await materializer.MaterializeDueAsync(CancellationToken.None)).RunsCreated);
        Assert.Equal(1L, await MaterializationTestKit.CountAsync(conn, "SELECT COUNT(*) FROM automation_runs"));
    }

    [Fact]
    public async Task DomainEventDispatch_materializes_matching_run_once()
    {
        var conn = await MaterializationTestKit.OpenAsync(":memory:");
        await using var _ = conn;
        await MaterializationTestKit.EnsureSchemaAsync(conn);

        var deTrigger = new TriggerDefinition("de_1", TriggerType.DomainEvent, true, null, null, null,
            null, null, null, null, null, null, "file.created", null);
        await MaterializationTestKit.SeedRevisionAsync(conn, AutomationId.Parse(Auto), AutomationRevisionId.Parse(Rev), deTrigger);
        var schedules = new TriggerScheduleRepository(conn);
        await schedules.UpsertAsync(AutomationId.Parse(Auto), AutomationRevisionId.Parse(Rev), deTrigger, nextOccurrenceAtUtc: null, T0, CancellationToken.None);

        // Seed a pending outbox event in space_1.
        await MaterializationTestKit.InsertOutboxAsync(conn, "evt_1", "file.created", "space_1",
            "{\"kind\":\"file.created\"}", T0);

        var store = new RunRepository(conn);
        var automations = new AutomationRepository(conn);
        var outbox = new OutboxRepository(conn);
        var dispatcher = new DomainEventDispatcher(outbox, schedules, automations, store, new SequentialIdGenerator(), new MutableClock { UtcNow = T0 });

        var dispatched = await dispatcher.DispatchPendingAsync(CancellationToken.None);
        Assert.Equal(1, dispatched);
        Assert.Equal(1L, await MaterializationTestKit.CountAsync(conn, "SELECT COUNT(*) FROM automation_runs"));

        // The outbox row must now be marked dispatched.
        Assert.Equal(1L, await MaterializationTestKit.CountAsync(conn,
            "SELECT COUNT(*) FROM domain_event_outbox WHERE id='evt_1' AND dispatched_at_utc IS NOT NULL"));

        // Idempotent: a second dispatch finds the event already dispatched → no new run.
        Assert.Equal(0, await dispatcher.DispatchPendingAsync(CancellationToken.None));
        Assert.Equal(1L, await MaterializationTestKit.CountAsync(conn, "SELECT COUNT(*) FROM automation_runs"));
    }

    [Fact]
    public async Task DomainEventDispatch_ignores_event_without_matching_trigger()
    {
        var conn = await MaterializationTestKit.OpenAsync(":memory:");
        await using var _ = conn;
        await MaterializationTestKit.EnsureSchemaAsync(conn);

        var deTrigger = new TriggerDefinition("de_1", TriggerType.DomainEvent, true, null, null, null,
            null, null, null, null, null, null, "file.created", null);
        await MaterializationTestKit.SeedRevisionAsync(conn, AutomationId.Parse(Auto), AutomationRevisionId.Parse(Rev), deTrigger);
        var schedules = new TriggerScheduleRepository(conn);
        await schedules.UpsertAsync(AutomationId.Parse(Auto), AutomationRevisionId.Parse(Rev), deTrigger, nextOccurrenceAtUtc: null, T0, CancellationToken.None);

        // Event of a DIFFERENT type must not match.
        await MaterializationTestKit.InsertOutboxAsync(conn, "evt_1", "file.deleted", "space_1", "{}", T0);

        var store = new RunRepository(conn);
        var automations = new AutomationRepository(conn);
        var outbox = new OutboxRepository(conn);
        var dispatcher = new DomainEventDispatcher(outbox, schedules, automations, store, new SequentialIdGenerator(), new MutableClock { UtcNow = T0 });

        Assert.Equal(0, await dispatcher.DispatchPendingAsync(CancellationToken.None));
        Assert.Equal(0L, await MaterializationTestKit.CountAsync(conn, "SELECT COUNT(*) FROM automation_runs"));
    }
}
