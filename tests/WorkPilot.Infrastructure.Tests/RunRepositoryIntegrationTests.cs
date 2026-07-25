using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using WorkPilot.Application.Automation.Run;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation.Run;
using WorkPilot.Infrastructure.Automation;
using WorkPilot.Infrastructure.Data;
using Xunit;

namespace WorkPilot.Infrastructure.Tests;

public class RunRepositoryIntegrationTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const string Canonical = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static async Task<SqliteConnection> OpenAsync(string dataSource)
    {
        var conn = new SqliteConnection($"Data Source={dataSource}");
        await conn.OpenAsync();
        await using (var fk = conn.CreateCommand()) { fk.CommandText = "PRAGMA foreign_keys=ON"; await fk.ExecuteNonQueryAsync(); }
        await using (var bt = conn.CreateCommand()) { bt.CommandText = "PRAGMA busy_timeout=5000"; await bt.ExecuteNonQueryAsync(); }
        if (dataSource != ":memory:")
            await using (var j = conn.CreateCommand()) { j.CommandText = "PRAGMA journal_mode=WAL"; await j.ExecuteNonQueryAsync(); }
        return conn;
    }

    private static async Task EnsureSchemaAsync(SqliteConnection conn)
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
    }

    private static async Task SeedAutomationAsync(SqliteConnection conn, string automationId, string revisionId, string spaceId = "space_1")
    {
        // automation_revisions.automation_id -> automation_definitions(id), so the definition must
        // exist first (with a NULL current_revision_id to break the circular FK), then the revision,
        // then point the definition at it (mirrors Migration 017's apply order).
        await using (var def = conn.CreateCommand())
        {
            def.CommandText = "INSERT INTO automation_definitions(id,space_id,name,description,lifecycle,current_revision_id,revision_number,created_at_utc,updated_at_utc,row_version) VALUES($aid,$sid,'Test','','enabled',NULL,1,$now,$now,1)";
            def.Parameters.AddWithValue("$aid", automationId);
            def.Parameters.AddWithValue("$sid", spaceId);
            def.Parameters.AddWithValue("$now", Now.ToString("O"));
            await def.ExecuteNonQueryAsync();
        }
        await using (var rev = conn.CreateCommand())
        {
            rev.CommandText = "INSERT INTO automation_revisions(id,automation_id,revision_number,schema_version,trigger_json,workflow_json,binding_json,budget_json,overlap_policy,missed_run_policy,permission_request_json,canonical_sha256,created_at_utc) VALUES($id,$aid,1,1,'{}','{}','{}','{}','skip','skip','{}',$canon,$now)";
            rev.Parameters.AddWithValue("$id", revisionId);
            rev.Parameters.AddWithValue("$aid", automationId);
            rev.Parameters.AddWithValue("$canon", Canonical);
            rev.Parameters.AddWithValue("$now", Now.ToString("O"));
            await rev.ExecuteNonQueryAsync();
        }
        await using (var link = conn.CreateCommand())
        {
            link.CommandText = "UPDATE automation_definitions SET current_revision_id=$rid WHERE id=$aid";
            link.Parameters.AddWithValue("$rid", revisionId);
            link.Parameters.AddWithValue("$aid", automationId);
            await link.ExecuteNonQueryAsync();
        }
    }

    private static (AutomationRun run, RunSnapshot snap) BuildRun(string runId, string snapId, string revisionId, DateTimeOffset started, string? automationId = null)
    {
        var snap = RunSnapshot.Create(RunSnapshotId.Parse(snapId), AutomationRevisionId.Parse(revisionId),
            ExpertRevisionId.Parse("exp_rev_1"), "{}", "{}", "{}", "{}", "{}", 0, "{\"v\":1}", Canonical, Now);
        var run = AutomationRun.Create(RunId.Parse(runId), AutomationRevisionId.Parse(revisionId),
            RunSnapshotId.Parse(snapId), RunTriggerKind.Interval, Now, Now,
            automationId: automationId is null ? null : AutomationId.Parse(automationId));
        run = run with { StartedAtUtc = started };
        return (run, snap);
    }

    [Fact]
    public async Task CreateRun_atomic_and_read_back_consistent()
    {
        var conn = await OpenAsync(":memory:");
        await using var _ = conn;
        await EnsureSchemaAsync(conn);
        await SeedAutomationAsync(conn, "auto_1", "rev_1");

        var repo = new RunRepository(conn);
        var (run, snap) = BuildRun("run_1", "snap_1", "rev_1", Now, "auto_1");
        var create = await repo.CreateRunAsync(run, snap, null, CancellationToken.None);
        Assert.True(create.IsSuccess, create.Error?.Code);

        var got = await repo.GetRunAsync(RunId.Parse("run_1"), CancellationToken.None);
        Assert.True(got.IsSuccess);
        var details = got.Value!;
        Assert.Equal(RunStatus.Queued, details.Run.Status);
        Assert.Equal("auto_1", details.Run.AutomationId!.Value);
        Assert.Equal(Canonical, details.Snapshot.CanonicalSha256);
        Assert.Empty(details.Steps);
        Assert.Empty(details.Events);
    }

    [Fact]
    public async Task CreateRun_fails_on_broken_foreign_key()
    {
        var conn = await OpenAsync(":memory:");
        await using var _ = conn;
        await EnsureSchemaAsync(conn);
        await SeedAutomationAsync(conn, "auto_1", "rev_1");

        var repo = new RunRepository(conn);
        // snapshot references a valid revision, but the run points at a non-existent revision => FK violation
        var snap = RunSnapshot.Create(RunSnapshotId.Parse("snap_1"), AutomationRevisionId.Parse("rev_1"),
            ExpertRevisionId.Parse("exp_rev_1"), "{}", "{}", "{}", "{}", "{}", 0, "{\"v\":1}", Canonical, Now);
        var run = AutomationRun.Create(RunId.Parse("run_1"), AutomationRevisionId.Parse("ghost_rev"),
            RunSnapshotId.Parse("snap_1"), RunTriggerKind.Interval, Now, Now, automationId: AutomationId.Parse("auto_1"));
        var create = await repo.CreateRunAsync(run, snap, null, CancellationToken.None);
        Assert.False(create.IsSuccess);
    }

    [Fact]
    public async Task DeleteRun_cascades_steps_and_events()
    {
        var conn = await OpenAsync(":memory:");
        await using var _ = conn;
        await EnsureSchemaAsync(conn);
        await SeedAutomationAsync(conn, "auto_1", "rev_1");

        var repo = new RunRepository(conn);
        var (run, snap) = BuildRun("run_1", "snap_1", "rev_1", Now, "auto_1");
        Assert.True((await repo.CreateRunAsync(run, snap, null, CancellationToken.None)).IsSuccess);

        // events via repo, a step via raw SQL (no step-writer in T07)
        await repo.AppendEventAsync(RunEvent.Create(RunEventId.Parse("e_1"), RunId.Parse("run_1"), "c",
            RunEventLevel.Info, "C", "Run.C", "{}", "corr", Now), CancellationToken.None);
        await using (var step = conn.CreateCommand())
        {
            step.CommandText = "INSERT INTO automation_step_runs(id,run_id,node_id,node_kind,status,idempotency_key,input_digest) VALUES('s_1','run_1','n1','agent_prompt','pending','idem','digest')";
            await step.ExecuteNonQueryAsync();
        }

        Assert.True((await repo.DeleteRunAsync(RunId.Parse("run_1"), CancellationToken.None)).IsSuccess);

        var stepCount = (int)await CountAsync(conn, "SELECT COUNT(*) FROM automation_step_runs WHERE run_id='run_1'");
        var eventCount = (int)await CountAsync(conn, "SELECT COUNT(*) FROM run_events WHERE run_id='run_1'");
        Assert.Equal(0, stepCount);
        Assert.Equal(0, eventCount);
    }

    [Fact]
    public async Task ListRuns_keyset_pagination_is_stable_and_complete()
    {
        var conn = await OpenAsync(":memory:");
        await using var _ = conn;
        await EnsureSchemaAsync(conn);
        await SeedAutomationAsync(conn, "auto_1", "rev_1");

        var repo = new RunRepository(conn);
        const int total = 53;
        for (var i = 0; i < total; i++)
        {
            var (run, snap) = BuildRun($"run_{i:000}", $"snap_{i:000}", "rev_1", Now.AddMinutes(-i), "auto_1");
            Assert.True((await repo.CreateRunAsync(run, snap, null, CancellationToken.None)).IsSuccess);
        }

        var collected = new List<RunListItem>();
        RunListCursor? cursor = null;
        do
        {
            var page = await repo.ListRunsAsync(new RunQuery(AutomationId: AutomationId.Parse("auto_1"), PageSize: 10, Cursor: cursor), CancellationToken.None);
            Assert.True(page.IsSuccess);
            collected.AddRange(page.Value!.Items);
            cursor = page.Value!.HasMore ? page.Value!.NextCursor : null;
        } while (cursor is not null);

        Assert.Equal(total, collected.Count);
        Assert.Equal(total, collected.Select(x => x.Id.Value).Distinct().Count());

        // Order must be (started_at_utc DESC, id DESC) => non-increasing started times.
        for (var i = 1; i < collected.Count; i++)
            Assert.True(collected[i - 1].StartedAtUtc >= collected[i].StartedAtUtc);
    }

    [Fact]
    public async Task AppendEvents_100k_persists_contiguous_sequences()
    {
        var conn = await OpenAsync(":memory:");
        await using var _ = conn;
        await EnsureSchemaAsync(conn);
        await SeedAutomationAsync(conn, "auto_1", "rev_1");

        var repo = new RunRepository(conn);
        var (run, snap) = BuildRun("run_1", "snap_1", "rev_1", Now, "auto_1");
        Assert.True((await repo.CreateRunAsync(run, snap, null, CancellationToken.None)).IsSuccess);

        const int total = 100_000;
        for (var batch = 0; batch < total / 1000; batch++)
        {
            var batchEvents = new List<RunEvent>();
            for (var i = 0; i < 1000; i++)
            {
                var seq = batch * 1000 + i;
                batchEvents.Add(RunEvent.Create(RunEventId.Parse($"e_{seq}"), RunId.Parse("run_1"), "tick",
                    RunEventLevel.Info, "TICK", "Run.Tick", "{}", $"corr_{seq}", Now));
            }
            Assert.True((await repo.AppendEventsAsync(batchEvents, CancellationToken.None)).IsSuccess);
        }

        var got = await repo.GetRunAsync(RunId.Parse("run_1"), CancellationToken.None);
        var loadedEvents = got.Value!.Events.OrderBy(e => e.Sequence).ToList();
        Assert.Equal(total, loadedEvents.Count);
        for (var i = 0; i < total; i++)
            Assert.Equal(i + 1, loadedEvents[i].Sequence);
    }

    [Fact]
    public async Task AppendEvent_concurrent_sequences_unique_and_contiguous()
    {
        var filePath = Path.GetTempFileName();
        SqliteConnection? main = null;
        try
        {
            main = await OpenAsync(filePath);
            await EnsureSchemaAsync(main);
            await SeedAutomationAsync(main, "auto_1", "rev_1");
            var (run, snap) = BuildRun("run_1", "snap_1", "rev_1", Now, "auto_1");
            Assert.True((await new RunRepository(main).CreateRunAsync(run, snap, null, CancellationToken.None)).IsSuccess);
            await main.CloseAsync();

            var counter = 0;
            var tasks = new List<Task>();
            for (var i = 0; i < 50; i++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    var c = await OpenAsync(filePath);
                    var r = new RunRepository(c);
                    var eid = RunEventId.Parse($"e_{Interlocked.Increment(ref counter)}");
                    var ev = RunEvent.Create(eid, RunId.Parse("run_1"), "tick", RunEventLevel.Info, "TICK", "Run.Tick", "{}", "corr", Now);
                    await r.AppendEventAsync(ev, CancellationToken.None);
                    await c.CloseAsync();
                }));
            }
            await Task.WhenAll(tasks);

            var read = await OpenAsync(filePath);
            var details = (await new RunRepository(read).GetRunAsync(RunId.Parse("run_1"), CancellationToken.None)).Value!;
            var seqs = details.Events.Select(e => e.Sequence).OrderBy(s => s).ToList();
            Assert.Equal(50, seqs.Count);
            Assert.Equal(50, seqs.Distinct().Count());
            for (var i = 0; i < 50; i++)
                Assert.Equal(i + 1, seqs[i]);
        }
        finally
        {
            try { File.Delete(filePath); File.Delete(filePath + "-wal"); File.Delete(filePath + "-shm"); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task CreateRun_survives_connection_restart()
    {
        var filePath = Path.GetTempFileName();
        try
        {
            var conn = await OpenAsync(filePath);
            await EnsureSchemaAsync(conn);
            await SeedAutomationAsync(conn, "auto_1", "rev_1");
            var repo = new RunRepository(conn);
            var (run, snap) = BuildRun("run_1", "snap_1", "rev_1", Now, "auto_1");
            Assert.True((await repo.CreateRunAsync(run, snap, null, CancellationToken.None)).IsSuccess);
            await conn.CloseAsync();

            // Simulate a restart: a brand-new connection to the same file.
            var reopened = await OpenAsync(filePath);
            var reread = await new RunRepository(reopened).GetRunAsync(RunId.Parse("run_1"), CancellationToken.None);
            Assert.True(reread.IsSuccess);
            var details = reread.Value!;
            Assert.Equal(RunStatus.Queued, details.Run.Status);
            Assert.Equal("auto_1", details.Run.AutomationId!.Value);
            Assert.Equal(Canonical, details.Snapshot.CanonicalSha256);
            Assert.Equal("rev_1", details.Run.AutomationRevisionId.Value);
            await reopened.CloseAsync();
        }
        finally
        {
            try { File.Delete(filePath); File.Delete(filePath + "-wal"); File.Delete(filePath + "-shm"); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task TryClaim_is_atomic_and_not_double_claimable()
    {
        var conn = await OpenAsync(":memory:");
        await using var _ = conn;
        await EnsureSchemaAsync(conn);
        await SeedAutomationAsync(conn, "auto_1", "rev_1");
        var repo = new RunRepository(conn);
        var (run, snap) = BuildRun("run_1", "snap_1", "rev_1", Now, "auto_1");
        Assert.True((await repo.CreateRunAsync(run, snap, null, CancellationToken.None)).IsSuccess);

        Assert.True((await repo.TryClaimAsync(RunId.Parse("run_1"), "worker_a", Now.AddMinutes(1), CancellationToken.None)).Value);
        // Second claim on the same (now claimed) run must fail.
        Assert.False((await repo.TryClaimAsync(RunId.Parse("run_1"), "worker_b", Now.AddMinutes(1), CancellationToken.None)).Value);

        var got = await repo.GetRunAsync(RunId.Parse("run_1"), CancellationToken.None);
        Assert.Equal(RunStatus.Claimed, got.Value!.Run.Status);
        Assert.Equal("worker_a", got.Value!.Run.LeaseOwner);
    }

    [Fact]
    public async Task UpsertStep_inserts_then_updates_same_logical_row()
    {
        var conn = await OpenAsync(":memory:");
        await using var _ = conn;
        await EnsureSchemaAsync(conn);
        await SeedAutomationAsync(conn, "auto_1", "rev_1");

        var repo = new RunRepository(conn);
        var (run, snap) = BuildRun("run_1", "snap_1", "rev_1", Now, "auto_1");
        Assert.True((await repo.CreateRunAsync(run, snap, null, CancellationToken.None)).IsSuccess);

        var step = StepRun.Create(StepRunId.Parse("s_1"), RunId.Parse("run_1"), "n1", "agent_prompt",
            "idem_n1", "digest_n1");
        Assert.True((await repo.UpsertStepAsync(step, CancellationToken.None)).IsSuccess);

        var stored = (await repo.GetRunAsync(RunId.Parse("run_1"), CancellationToken.None)).Value!;
        var first = stored.Steps.Single(s => s.NodeId == "n1");
        Assert.Equal(StepRunStatus.Pending, first.Status);

        // Advance and re-upsert: same StepRunId (idempotency key/attempt unchanged) => UPDATE, not INSERT.
        var advanced = step with { Status = StepRunStatus.Succeeded, RowVersion = 2, StartedAtUtc = Now, FinishedAtUtc = Now };
        Assert.True((await repo.UpsertStepAsync(advanced, CancellationToken.None)).IsSuccess);

        var updated = (await repo.GetRunAsync(RunId.Parse("run_1"), CancellationToken.None)).Value!;
        Assert.Single(updated.Steps); // not duplicated
        Assert.Equal(StepRunStatus.Succeeded, updated.Steps.Single(s => s.NodeId == "n1").Status);
    }

    [Fact]
    public async Task PersistExecutionResult_writes_header_steps_and_contiguous_events()
    {
        var conn = await OpenAsync(":memory:");
        await using var _ = conn;
        await EnsureSchemaAsync(conn);
        await SeedAutomationAsync(conn, "auto_1", "rev_1");

        var repo = new RunRepository(conn);
        var (run, snap) = BuildRun("run_1", "snap_1", "rev_1", Now, "auto_1");
        Assert.True((await repo.CreateRunAsync(run, snap, null, CancellationToken.None)).IsSuccess);

        var finalRun = run with
        {
            Status = RunStatus.Completed,
            ModelTurnCount = 3,
            CapabilityCallCount = 1,
            ResultBytes = 512,
            LastEventSequence = 2
        };
        var steps = new List<StepRun>
        {
            StepRun.Create(StepRunId.Parse("s_1"), RunId.Parse("run_1"), "n1", "agent_prompt", "idem_n1", "digest_n1")
                with { Status = StepRunStatus.Succeeded, RowVersion = 2, StartedAtUtc = Now, FinishedAtUtc = Now },
            StepRun.Create(StepRunId.Parse("s_2"), RunId.Parse("run_1"), "n2", "notification", "idem_n2", "digest_n2")
                with { Status = StepRunStatus.Succeeded, RowVersion = 2, StartedAtUtc = Now, FinishedAtUtc = Now }
        };
        var events = new List<RunEvent>
        {
            RunEvent.Create(RunEventId.Parse("e_1"), RunId.Parse("run_1"), "step_succeeded", RunEventLevel.Info,
                "RUN_STEP_SUCCEEDED", "Run.StepSucceeded", "{\"node_id\":\"n1\"}", "corr", Now, StepRunId.Parse("s_1"), 1),
            RunEvent.Create(RunEventId.Parse("e_2"), RunId.Parse("run_1"), "run_completed", RunEventLevel.Info,
                "RUN_COMPLETED", "Run.Completed", "{}", "corr", Now)
        };

        var persist = await repo.PersistExecutionResultAsync(finalRun, steps, events, CancellationToken.None);
        Assert.True(persist.IsSuccess, persist.Error?.Code);

        var details = (await repo.GetRunAsync(RunId.Parse("run_1"), CancellationToken.None)).Value!;
        Assert.Equal(RunStatus.Completed, details.Run.Status);
        Assert.Equal(3, details.Run.ModelTurnCount);
        Assert.Equal(1, details.Run.CapabilityCallCount);
        Assert.Equal(2, details.Steps.Count);
        Assert.All(details.Steps, s => Assert.Equal(StepRunStatus.Succeeded, s.Status));
        Assert.Equal(2, details.Events.Count);
        Assert.Equal(1, details.Events.Min(e => e.Sequence));
        Assert.Equal(2, details.Events.Max(e => e.Sequence));
    }

    private static async Task<long> CountAsync(SqliteConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
}
