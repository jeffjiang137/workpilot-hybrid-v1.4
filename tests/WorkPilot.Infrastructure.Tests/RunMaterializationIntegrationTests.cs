using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using WorkPilot.Application.Automation.Materialization;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation.Run;
using WorkPilot.Domain.Automation.Run.Materialization;
using WorkPilot.Infrastructure.Automation;
using WorkPilot.Infrastructure.Automation.Materialization;
using Xunit;

namespace WorkPilot.Infrastructure.Tests;

/// <summary>RUN-001/002/009/010: the materialization store (SQLite) must be idempotent, claim atomically, and recover on crash.</summary>
public class RunMaterializationIntegrationTests
{
    private const string AutoA = "auto_1";
    private const string AutoB = "auto_2";
    private const string Rev = "rev_1";
    private const string RevB = "rev_2";
    private static readonly DateTimeOffset T = MaterializationTestKit.Now;

    private static async Task<(SqliteConnection conn, RunRepository repo)> FixtureAsync(string automationId = AutoA)
    {
        var conn = await MaterializationTestKit.OpenAsync(":memory:");
        await MaterializationTestKit.EnsureSchemaAsync(conn);
        await MaterializationTestKit.SeedRevisionAsync(conn, AutomationId.Parse(automationId), AutomationRevisionId.Parse(Rev));
        return (conn, new RunRepository(conn));
    }

    [Fact]
    public async Task TryReserveOccurrence_is_idempotent_no_double_row()
    {
        var (conn, repo) = await FixtureAsync();
        await using var _ = conn;
        var key = TriggerOccurrenceDedupe.Compute(AutomationId.Parse(AutoA), AutomationRevisionId.Parse(Rev), "t1", T);
        var occ = MaterializationTestKit.MakeOccurrence(key, AutoA, Rev, "t1", T);

        Assert.True((await repo.TryReserveOccurrenceAsync(occ, CancellationToken.None)).Value);
        Assert.False((await repo.TryReserveOccurrenceAsync(occ, CancellationToken.None)).Value); // same dedupe key
        Assert.Equal(1L, await MaterializationTestKit.CountAsync(conn, "SELECT COUNT(*) FROM automation_trigger_occurrences"));
    }

    [Fact]
    public async Task CreateRunForOccurrence_persists_run_snapshot_and_event()
    {
        var (conn, repo) = await FixtureAsync();
        await using var _ = conn;
        var key = TriggerOccurrenceDedupe.Compute(AutomationId.Parse(AutoA), AutomationRevisionId.Parse(Rev), "t1", T);
        var occ = MaterializationTestKit.MakeOccurrence(key, AutoA, Rev, "t1", T);
        Assert.True((await repo.TryReserveOccurrenceAsync(occ, CancellationToken.None)).Value);

        var (run, snap) = MaterializationTestKit.BuildRun("run_1", "snap_1", Rev, T, AutoA);
        run = run with { OccurrenceId = occ.Id };
        var ev = RunEvent.Create(RunEventId.Parse("e_1"), RunId.Parse("run_1"), "run_created",
            RunEventLevel.Info, "MATERIALIZED", "Run.Created", "{}", "corr", T);

        Assert.True((await repo.CreateRunForOccurrenceAsync(run, snap, ev, CancellationToken.None)).IsSuccess);
        Assert.Equal(1L, await MaterializationTestKit.CountAsync(conn, "SELECT COUNT(*) FROM automation_runs WHERE id='run_1' AND status='queued'"));
        Assert.Equal(1L, await MaterializationTestKit.CountAsync(conn, "SELECT COUNT(*) FROM automation_run_snapshots WHERE id='snap_1'"));
        Assert.Equal(1L, await MaterializationTestKit.CountAsync(conn, "SELECT COUNT(*) FROM run_events WHERE run_id='run_1'"));
    }

    [Fact]
    public async Task RecordCoalesce_bumps_count_and_appends_event()
    {
        var (conn, repo) = await FixtureAsync();
        await using var _ = conn;
        var key = TriggerOccurrenceDedupe.Compute(AutomationId.Parse(AutoA), AutomationRevisionId.Parse(Rev), "t1", T);
        var occ = MaterializationTestKit.MakeOccurrence(key, AutoA, Rev, "t1", T);
        Assert.True((await repo.TryReserveOccurrenceAsync(occ, CancellationToken.None)).Value);
        var (run, snap) = MaterializationTestKit.BuildRun("run_1", "snap_1", Rev, T, AutoA);
        run = run with { OccurrenceId = occ.Id };
        var created = RunEvent.Create(RunEventId.Parse("e_1"), RunId.Parse("run_1"), "run_created", RunEventLevel.Info, "MATERIALIZED", "Run.Created", "{}", "corr", T);
        Assert.True((await repo.CreateRunForOccurrenceAsync(run, snap, created, CancellationToken.None)).IsSuccess);

        var coalescedEvent = RunEvent.Create(RunEventId.Parse("e_2"), RunId.Parse("run_1"), "coalesced", RunEventLevel.Info, "MATERIALIZED", "Run.Coalesced", "{}", "corr", T);
        Assert.True((await repo.RecordCoalesceAsync(RunId.Parse("run_1"), 2, occ, coalescedEvent, CancellationToken.None)).IsSuccess);

        var got = (await repo.GetRunAsync(RunId.Parse("run_1"), CancellationToken.None)).Value!;
        Assert.Equal(2, got.Run.CoalescedCount);
        Assert.Equal(2L, await MaterializationTestKit.CountAsync(conn, "SELECT COUNT(*) FROM run_events WHERE run_id='run_1'"));
    }

    [Fact]
    public async Task GetActiveRuns_excludes_terminal()
    {
        var (conn, repo) = await FixtureAsync();
        await using var _ = conn;
        var (run, snap) = MaterializationTestKit.BuildRun("run_1", "snap_1", Rev, T, AutoA);
        Assert.True((await repo.CreateRunAsync(run, snap, null, CancellationToken.None)).IsSuccess);
        var (term, termSnap) = MaterializationTestKit.BuildRun("run_term", "snap_term", Rev, T, AutoA);
        Assert.True((await repo.CreateRunAsync(term, termSnap, null, CancellationToken.None)).IsSuccess);
        await MaterializationTestKit.SetRunStatusAsync(conn, "run_term", "completed");

        var active = (await repo.GetActiveRunsAsync(AutomationId.Parse(AutoA), CancellationToken.None)).Value!;
        Assert.Contains(active, a => a.Id == RunId.Parse("run_1"));
        Assert.DoesNotContain(active, a => a.Id == RunId.Parse("run_term"));
    }

    [Fact]
    public async Task GetClaimableQueued_respects_available_and_status()
    {
        var (conn, repo) = await FixtureAsync();
        await using var _ = conn;
        var (ready, snap1) = MaterializationTestKit.BuildRun("run_ready", "snap_r", Rev, T, AutoA); // available now
        var (future, snap2) = MaterializationTestKit.BuildRun("run_future", "snap_f", Rev, T.AddHours(1), AutoA); // available +1h
        Assert.True((await repo.CreateRunAsync(ready, snap1, null, CancellationToken.None)).IsSuccess);
        Assert.True((await repo.CreateRunAsync(future, snap2, null, CancellationToken.None)).IsSuccess);

        var claimable = (await repo.GetClaimableQueuedAsync(T, 50, CancellationToken.None)).Value!;
        Assert.Contains(claimable, c => c.Id == RunId.Parse("run_ready"));
        Assert.DoesNotContain(claimable, c => c.Id == RunId.Parse("run_future"));
    }

    [Fact]
    public async Task ClaimBatch_claims_distinct_automations()
    {
        var (conn, repo) = await FixtureAsync();
        await using var _ = conn;
        await MaterializationTestKit.SeedRevisionAsync(conn, AutomationId.Parse(AutoB), AutomationRevisionId.Parse(RevB));
        var (r1, s1) = MaterializationTestKit.BuildRun("run_a", "snap_a", Rev, T, AutoA);
        var (r2, s2) = MaterializationTestKit.BuildRun("run_b", "snap_b", RevB, T, AutoB);
        Assert.True((await repo.CreateRunAsync(r1, s1, null, CancellationToken.None)).IsSuccess);
        Assert.True((await repo.CreateRunAsync(r2, s2, null, CancellationToken.None)).IsSuccess);

        var claimed = (await repo.ClaimBatchAsync(new[] { RunId.Parse("run_a"), RunId.Parse("run_b") }, "w1", T.AddMinutes(5), T, CancellationToken.None)).Value!;
        Assert.Equal(2, claimed.Count);
        var got = (await repo.GetRunAsync(RunId.Parse("run_a"), CancellationToken.None)).Value!;
        Assert.Equal(RunStatus.Claimed, got.Run.Status);
        Assert.Equal("w1", got.Run.LeaseOwner);
    }

    [Fact]
    public async Task ClaimBatch_respects_per_automation_guard_against_active_sibling()
    {
        var (conn, repo) = await FixtureAsync();
        await using var _ = conn;
        // Two runs of the SAME automation; claim the first, then attempt to claim the second.
        var (r1, s1) = MaterializationTestKit.BuildRun("run_a1", "snap_a1", Rev, T, AutoA);
        var (r2, s2) = MaterializationTestKit.BuildRun("run_a2", "snap_a2", Rev, T, AutoA);
        Assert.True((await repo.CreateRunAsync(r1, s1, null, CancellationToken.None)).IsSuccess);
        Assert.True((await repo.CreateRunAsync(r2, s2, null, CancellationToken.None)).IsSuccess);

        Assert.True((await repo.ClaimBatchAsync(new[] { RunId.Parse("run_a1") }, "w1", T.AddMinutes(5), T, CancellationToken.None)).Value!.Count == 1);
        // Second run of the same automation must be blocked by the NOT EXISTS active-sibling guard.
        var second = (await repo.ClaimBatchAsync(new[] { RunId.Parse("run_a2") }, "w2", T.AddMinutes(5), T, CancellationToken.None)).Value!;
        Assert.Empty(second);
    }

    [Fact]
    public async Task ClaimBatch_excludes_already_claimed_and_terminal()
    {
        var (conn, repo) = await FixtureAsync();
        await using var _ = conn;
        await MaterializationTestKit.SeedRevisionAsync(conn, AutomationId.Parse(AutoB), AutomationRevisionId.Parse(RevB));
        var (r1, s1) = MaterializationTestKit.BuildRun("run_a", "snap_a", Rev, T, AutoA);
        var (r2, s2) = MaterializationTestKit.BuildRun("run_b", "snap_b", RevB, T, AutoB);
        Assert.True((await repo.CreateRunAsync(r1, s1, null, CancellationToken.None)).IsSuccess);
        Assert.True((await repo.CreateRunAsync(r2, s2, null, CancellationToken.None)).IsSuccess);
        Assert.True((await repo.ClaimBatchAsync(new[] { RunId.Parse("run_a") }, "w1", T.AddMinutes(5), T, CancellationToken.None)).Value!.Count == 1);

        // Re-claiming an already-claimed run returns empty.
        Assert.Empty((await repo.ClaimBatchAsync(new[] { RunId.Parse("run_a") }, "wX", T.AddMinutes(5), T, CancellationToken.None)).Value!);
        // The other run is still claimable.
        Assert.Single((await repo.ClaimBatchAsync(new[] { RunId.Parse("run_b") }, "wY", T.AddMinutes(5), T, CancellationToken.None)).Value!);
    }

    [Fact]
    public async Task Heartbeat_extends_lease_for_owner()
    {
        var (conn, repo) = await FixtureAsync();
        await using var _ = conn;
        var (r1, s1) = MaterializationTestKit.BuildRun("run_a", "snap_a", Rev, T, AutoA);
        Assert.True((await repo.CreateRunAsync(r1, s1, null, CancellationToken.None)).IsSuccess);
        Assert.True((await repo.ClaimBatchAsync(new[] { RunId.Parse("run_a") }, "w1", T.AddMinutes(5), T, CancellationToken.None)).Value!.Count == 1);

        Assert.True((await repo.HeartbeatAsync("w1", T.AddMinutes(30), new[] { RunId.Parse("run_a") }, CancellationToken.None)).IsSuccess);
        var got = (await repo.GetRunAsync(RunId.Parse("run_a"), CancellationToken.None)).Value!;
        Assert.Equal(T.AddMinutes(30), got.Run.LeaseExpiresAtUtc);
    }

    [Fact]
    public async Task ReleaseLease_requeues_claimed_run()
    {
        var (conn, repo) = await FixtureAsync();
        await using var _ = conn;
        var (r1, s1) = MaterializationTestKit.BuildRun("run_a", "snap_a", Rev, T, AutoA);
        Assert.True((await repo.CreateRunAsync(r1, s1, null, CancellationToken.None)).IsSuccess);
        Assert.True((await repo.ClaimBatchAsync(new[] { RunId.Parse("run_a") }, "w1", T.AddMinutes(5), T, CancellationToken.None)).Value!.Count == 1);

        Assert.True((await repo.ReleaseLeaseAsync(RunId.Parse("run_a"), CancellationToken.None)).IsSuccess);
        var got = (await repo.GetRunAsync(RunId.Parse("run_a"), CancellationToken.None)).Value!;
        Assert.Equal(RunStatus.Queued, got.Run.Status);
        Assert.Null(got.Run.LeaseOwner);
    }

    [Fact]
    public async Task ScanExpiredLeases_returns_expired_active_runs()
    {
        var (conn, repo) = await FixtureAsync();
        await using var _ = conn;
        var (r1, s1) = MaterializationTestKit.BuildRun("run_a", "snap_a", Rev, T, AutoA);
        Assert.True((await repo.CreateRunAsync(r1, s1, null, CancellationToken.None)).IsSuccess);
        // Claim with an already-expired lease.
        Assert.True((await repo.ClaimBatchAsync(new[] { RunId.Parse("run_a") }, "w1", T.AddMinutes(-1), T, CancellationToken.None)).Value!.Count == 1);

        var expired = (await repo.ScanExpiredLeasesAsync(T, 50, CancellationToken.None)).Value!;
        Assert.Single(expired);
        Assert.Equal(RunId.Parse("run_a"), expired[0].RunId);
    }

    [Fact]
    public async Task RecoverLease_requeues_when_no_side_effect()
    {
        var (conn, repo) = await FixtureAsync();
        await using var _ = conn;
        var (r1, s1) = MaterializationTestKit.BuildRun("run_a", "snap_a", Rev, T, AutoA);
        Assert.True((await repo.CreateRunAsync(r1, s1, null, CancellationToken.None)).IsSuccess);
        Assert.True((await repo.ClaimBatchAsync(new[] { RunId.Parse("run_a") }, "w1", T.AddMinutes(-1), T, CancellationToken.None)).Value!.Count == 1);

        Assert.True((await repo.RecoverLeaseAsync(RunId.Parse("run_a"), T, sideEffectInFlight: false, CancellationToken.None)).IsSuccess);
        var got = (await repo.GetRunAsync(RunId.Parse("run_a"), CancellationToken.None)).Value!;
        Assert.Equal(RunStatus.Queued, got.Run.Status); // requeued, ready for another worker
        Assert.Equal(1, got.Run.RecoveryCount);
    }

    [Fact]
    public async Task RecoverLease_marks_needs_review_when_side_effect_in_flight()
    {
        var (conn, repo) = await FixtureAsync();
        await using var _ = conn;
        var (r1, s1) = MaterializationTestKit.BuildRun("run_a", "snap_a", Rev, T, AutoA);
        Assert.True((await repo.CreateRunAsync(r1, s1, null, CancellationToken.None)).IsSuccess);
        Assert.True((await repo.ClaimBatchAsync(new[] { RunId.Parse("run_a") }, "w1", T.AddMinutes(-1), T, CancellationToken.None)).Value!.Count == 1);

        Assert.True((await repo.RecoverLeaseAsync(RunId.Parse("run_a"), T, sideEffectInFlight: true, CancellationToken.None)).IsSuccess);
        var got = (await repo.GetRunAsync(RunId.Parse("run_a"), CancellationToken.None)).Value!;
        Assert.Equal(RunStatus.NeedsReview, got.Run.Status);
    }

    [Fact]
    public async Task RecoverLease_fails_after_recovery_cap()
    {
        var (conn, repo) = await FixtureAsync();
        await using var _ = conn;
        var (r1, s1) = MaterializationTestKit.BuildRun("run_a", "snap_a", Rev, T, AutoA);
        Assert.True((await repo.CreateRunAsync(r1, s1, null, CancellationToken.None)).IsSuccess);
        Assert.True((await repo.ClaimBatchAsync(new[] { RunId.Parse("run_a") }, "w1", T.AddMinutes(-1), T, CancellationToken.None)).Value!.Count == 1);
        // Pre-set recovery_count at/above the cap (3) via raw SQL.
        await MaterializationTestKit.SetRecoveryCountAsync(conn, "run_a", 3);

        Assert.True((await repo.RecoverLeaseAsync(RunId.Parse("run_a"), T, sideEffectInFlight: false, CancellationToken.None)).IsSuccess);
        var got = (await repo.GetRunAsync(RunId.Parse("run_a"), CancellationToken.None)).Value!;
        Assert.Equal(RunStatus.Failed, got.Run.Status);
        Assert.Equal("repeated_worker_crash", got.Run.FinalErrorCode);
    }

    [Fact]
    public async Task AppendEvents_via_store_records_sequences()
    {
        var (conn, repo) = await FixtureAsync();
        await using var _ = conn;
        var (r1, s1) = MaterializationTestKit.BuildRun("run_a", "snap_a", Rev, T, AutoA);
        Assert.True((await repo.CreateRunAsync(r1, s1, null, CancellationToken.None)).IsSuccess);
        var events = new[]
        {
            RunEvent.Create(RunEventId.Parse("e_1"), RunId.Parse("run_a"), "t", RunEventLevel.Info, "X", "Run.T", "{}", "c", T),
            RunEvent.Create(RunEventId.Parse("e_2"), RunId.Parse("run_a"), "t", RunEventLevel.Info, "X", "Run.T", "{}", "c", T),
        };
        Assert.True((await repo.AppendEventsAsync(events, CancellationToken.None)).IsSuccess);
        var got = (await repo.GetRunAsync(RunId.Parse("run_a"), CancellationToken.None)).Value!;
        Assert.Equal(2, got.Events.Count);
        Assert.Equal(1, got.Events.Min(e => e.Sequence));
        Assert.Equal(2, got.Events.Max(e => e.Sequence));
    }
}
