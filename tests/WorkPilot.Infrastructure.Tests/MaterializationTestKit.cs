using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using WorkPilot.Application.Automation;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation;
using WorkPilot.Domain.Automation.Run;
using WorkPilot.Domain.Automation.Scheduling;
using WorkPilot.Infrastructure.Automation;
using WorkPilot.Infrastructure.Automation.Materialization;
using WorkPilot.Infrastructure.Data;
using Xunit;

namespace WorkPilot.Infrastructure.Tests;

/// <summary>Shared bootstrap + factory helpers for T09 materialization integration tests.</summary>
internal static class MaterializationTestKit
{
    public static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public const string Canonical = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    public static async Task<SqliteConnection> OpenAsync(string dataSource)
    {
        var conn = new SqliteConnection($"Data Source={dataSource}");
        await conn.OpenAsync();
        await using (var fk = conn.CreateCommand()) { fk.CommandText = "PRAGMA foreign_keys=ON"; await fk.ExecuteNonQueryAsync(); }
        await using (var bt = conn.CreateCommand()) { bt.CommandText = "PRAGMA busy_timeout=5000"; await bt.ExecuteNonQueryAsync(); }
        if (dataSource != ":memory:")
            await using (var j = conn.CreateCommand()) { j.CommandText = "PRAGMA journal_mode=WAL"; await j.ExecuteNonQueryAsync(); }
        return conn;
    }

    public static async Task EnsureSchemaAsync(SqliteConnection conn)
    {
        await using (var spaces = conn.CreateCommand())
        {
            spaces.CommandText = "CREATE TABLE IF NOT EXISTS spaces(id TEXT PRIMARY KEY, is_default INTEGER NOT NULL DEFAULT 0)";
            await spaces.ExecuteNonQueryAsync();
        }
        await using (var exp = conn.CreateCommand())
        {
            exp.CommandText = "CREATE TABLE IF NOT EXISTS expert_revisions(id TEXT PRIMARY KEY, expert_id TEXT NOT NULL, revision_number INTEGER NOT NULL)";
            await exp.ExecuteNonQueryAsync();
        }
        var migrator = new V15DatabaseMigrator(new FakeClock(Now));
        await migrator.CreateTablesAsync(conn);
        await migrator.CreateRunTablesAsync(conn);
        await using (var seedSpace = conn.CreateCommand())
        {
            seedSpace.CommandText = "INSERT OR IGNORE INTO spaces(id,is_default) VALUES('space_1',1)";
            await seedSpace.ExecuteNonQueryAsync();
        }
        await using (var seedExp = conn.CreateCommand())
        {
            seedExp.CommandText = "INSERT OR IGNORE INTO expert_revisions(id,expert_id,revision_number) VALUES('exp_rev_1','exp_1',1)";
            await seedExp.ExecuteNonQueryAsync();
        }
        await using (var seedUnbound = conn.CreateCommand())
        {
            // The materializer uses ExpertRevisionId.Parse("unbound") when a revision is unbound
            // (Binding.ExpertId == null). Seed the sentinel so the snapshot FK holds.
            seedUnbound.CommandText = "INSERT OR IGNORE INTO expert_revisions(id,expert_id,revision_number) VALUES('unbound','unbound',0)";
            await seedUnbound.ExecuteNonQueryAsync();
        }
    }

    /// <summary>Seeds a revision built from Domain factories (valid trigger/workflow/binding/budget) so the materializer can deserialize it.</summary>
    public static async Task SeedRevisionAsync(SqliteConnection conn, AutomationId automationId, AutomationRevisionId revisionId, TriggerDefinition? trigger = null)
    {
        var rev = AutomationRevision.Create(revisionId, automationId, 1,
            trigger ?? IntervalTrigger(), SingleAgent(), Binding(), Budget(), OverlapPolicy.Skip, MissedRunPolicy.RunOnce,
            new PermissionRequest(Array.Empty<string>(), "read-only"), Now);
        var def = AutomationDefinition.Create(automationId, SpaceId.Parse("space_1"), "Test", "", revisionId, Now).Value!;
        var repo = new AutomationRepository(conn);
        Assert.True((await repo.SaveAsync(def, rev, CancellationToken.None)).IsSuccess);
    }

    public static (AutomationRun run, RunSnapshot snap) BuildRun(string runId, string snapId, string revisionId, DateTimeOffset availableAt, string? automationId = null)
    {
        var snap = BuildSnapshot(snapId, revisionId);
        var run = AutomationRun.Create(RunId.Parse(runId), AutomationRevisionId.Parse(revisionId),
            RunSnapshotId.Parse(snapId), RunTriggerKind.Interval, availableAt, availableAt,
            automationId: automationId is null ? null : AutomationId.Parse(automationId));
        return (run, snap);
    }

    public static RunSnapshot BuildSnapshot(string snapId, string revisionId)
        => RunSnapshot.Create(RunSnapshotId.Parse(snapId), AutomationRevisionId.Parse(revisionId),
            ExpertRevisionId.Parse("exp_rev_1"), "{}", "{}", "{}", "{}", "{}", 0, "{\"v\":1}", Canonical, Now);

    public static TriggerOccurrence MakeOccurrence(string dedupeKey, string automationId, string revisionId, string triggerId, DateTimeOffset scheduled)
        => TriggerOccurrence.Create(TriggerOccurrenceId.Parse("occ_" + dedupeKey[..8]),
            AutomationId.Parse(automationId), AutomationRevisionId.Parse(revisionId), triggerId, scheduled, Now,
            OccurrenceDisposition.Queued, dedupeKey, 0, "{\"type\":\"Interval\"}");

    // ---- Domain factory helpers ----
    public static TriggerDefinition IntervalTrigger(long intervalSeconds = 3600) => new(
        "interval_1", TriggerType.Interval, true, null, null, null, intervalSeconds,
        Now, null, null, null, null, null, null);

    public static WorkflowDefinition SingleAgent() => new(1, "agent_prompt_1",
        new[] { new WorkflowNode("agent_prompt_1", "指令", "agent_prompt", 60, false, null) },
        Array.Empty<WorkflowEdge>());

    public static AutomationBinding Binding() => new(null, null);

    public static RunBudget Budget(int maxModelTurns = 8, long maxTokens = 200_000) =>
        new(maxModelTurns, maxTokens, 3600, 100, 10_000_000);

    public static async Task<long> CountAsync(SqliteConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public static async Task SetRunStatusAsync(SqliteConnection conn, string runId, string status)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE automation_runs SET status=$s, row_version=row_version+1 WHERE id=$id";
        cmd.Parameters.AddWithValue("$s", status);
        cmd.Parameters.AddWithValue("$id", runId);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task SetRecoveryCountAsync(SqliteConnection conn, string runId, int count)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE automation_runs SET recovery_count=$c, row_version=row_version+1 WHERE id=$id";
        cmd.Parameters.AddWithValue("$c", count);
        cmd.Parameters.AddWithValue("$id", runId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Re-arms a schedule for an identical replay window (drives the dedupe idempotency test).</summary>
    public static async Task ResetScheduleAsync(SqliteConnection conn, string revisionId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE automation_schedules SET last_materialized_at_utc=NULL, next_occurrence_at_utc=$next, row_version=row_version+1 WHERE automation_revision_id=$rev";
        cmd.Parameters.AddWithValue("$next", Now.AddHours(1).ToString("O"));
        cmd.Parameters.AddWithValue("$rev", revisionId);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task InsertOutboxAsync(SqliteConnection conn, string id, string eventType, string spaceId, string payload, DateTimeOffset occurredAt)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO domain_event_outbox(id,event_type,space_id,entity_type,entity_id,entity_version,safe_payload_json,occurred_at_utc,attempt_count,next_attempt_at_utc,dispatched_at_utc)
            VALUES($id,$et,$sid,'file','file_1',1,$pl,$occ,0,NULL,NULL)
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$et", eventType);
        cmd.Parameters.AddWithValue("$sid", spaceId);
        cmd.Parameters.AddWithValue("$pl", payload);
        cmd.Parameters.AddWithValue("$occ", occurredAt.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <summary>UTC-only time zone so interval scheduling tests stay OS-independent.</summary>
internal sealed class UtcZone : IZone
{
    public string Id => "UTC";
    public TimeSpan GetUtcOffset(DateTimeOffset utc) => TimeSpan.Zero;
    public IReadOnlyList<(DateTimeOffset Utc, TimeSpan Offset)> ResolveLocal(DateTime local)
        => new[] { (new DateTimeOffset(local, TimeSpan.Zero), TimeSpan.Zero) };
}

internal sealed class UtcResolver : ITimeZoneResolver
{
    public IZone? Resolve(string _) => new UtcZone();
}

/// <summary>Mutable clock so claim/lease/recovery timing can be controlled within a test.</summary>
internal sealed class MutableClock : IClock
{
    public DateTimeOffset UtcNow { get; set; }
    public DateTimeOffset Now => UtcNow;
}

/// <summary>Deterministic id generator producing sortable, stable ids for tests.</summary>
internal sealed class SequentialIdGenerator : IIdGenerator
{
    private int _counter;
    public string NewId() => $"id_{++_counter:000000}";
}
