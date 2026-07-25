using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using WorkPilot.Application.Automation.Run;
using WorkPilot.Application.Security.Retention;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation.Run;
using WorkPilot.Domain.Security;
using WorkPilot.Domain.Security.Audit;
using WorkPilot.Domain.Security.Retention;
using WorkPilot.Infrastructure.Automation;
using WorkPilot.Infrastructure.Data;
using WorkPilot.Infrastructure.Security;
using Xunit;

namespace WorkPilot.Infrastructure.Tests;

/// <summary>
/// End-to-end coverage of Migration 022 (retention_settings) and the two retention stores
/// (doc 05 §9, SEC-106). Uses the migration helpers to build the minimal schema, then exercises
/// the settings singleton round-trip and every delete path of <see cref="SqliteRetentionStore"/>
/// against real SQLite.
/// </summary>
public class RetentionStoreIntegrationTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Old = Now.AddDays(-400); // beyond every retention window
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
        await migrator.CreateSecurityTablesAsync(conn);
        await migrator.CreateSecurityStateTablesAsync(conn);
        await migrator.CreateRetentionTablesAsync(conn);

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

    private static async Task SeedAutomationAsync(SqliteConnection conn, string automationId, string revisionId)
    {
        await using (var def = conn.CreateCommand())
        {
            def.CommandText = "INSERT INTO automation_definitions(id,space_id,name,description,lifecycle,current_revision_id,revision_number,created_at_utc,updated_at_utc,row_version) VALUES($aid,'space_1','Test','','enabled',NULL,1,$now,$now,1)";
            def.Parameters.AddWithValue("$aid", automationId);
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

    private static (AutomationRun run, RunSnapshot snap) BuildRun(string runId, string snapId, string revisionId, RunStatus status, DateTimeOffset finished)
    {
        var snap = RunSnapshot.Create(RunSnapshotId.Parse(snapId), AutomationRevisionId.Parse(revisionId),
            ExpertRevisionId.Parse("exp_rev_1"), "{}", "{}", "{}", "{}", "{}", 0, "{\"v\":1}", Canonical, Now);
        var run = AutomationRun.Create(RunId.Parse(runId), AutomationRevisionId.Parse(revisionId),
            RunSnapshotId.Parse(snapId), RunTriggerKind.Interval, Now, Now, automationId: AutomationId.Parse("auto_1"));
        run = run with { Status = status, StartedAtUtc = finished.AddMinutes(-1), FinishedAtUtc = finished };
        return (run, snap);
    }

    // ---------------------------------------------------------------- settings singleton

    [Fact]
    public async Task Settings_Get_missing_returns_default()
    {
        var conn = await OpenAsync(":memory:");
        await using var _ = conn;
        await EnsureSchemaAsync(conn);

        var store = new SqliteRetentionSettingsStore(conn, new FakeClock(Now));
        var get = await store.GetAsync(CancellationToken.None);

        Assert.True(get.IsSuccess);
        Assert.Equal(RetentionPolicy.Default.RunDays, get.Value!.Policy.RunDays);
    }

    [Fact]
    public async Task Settings_Save_then_get_round_trips_values()
    {
        var conn = await OpenAsync(":memory:");
        await using var _ = conn;
        await EnsureSchemaAsync(conn);

        var store = new SqliteRetentionSettingsStore(conn, new FakeClock(Now));
        // In-range values (clamping itself is covered at the RetentionSettingsService layer in the
        // Application tests; the store persists exactly what it is given, bounded by the schema CHECK).
        var settings = new RetentionSettings(new RetentionPolicy(120, 45, 300), Now);
        var save = await store.SaveAsync(settings, CancellationToken.None);
        Assert.True(save.IsSuccess);

        var get = await store.GetAsync(CancellationToken.None);
        Assert.True(get.IsSuccess);
        Assert.Equal(120, get.Value!.Policy.RunDays);
        Assert.Equal(45, get.Value!.Policy.EventDays);
        Assert.Equal(300, get.Value!.Policy.AuditDays);
    }

    [Fact]
    public async Task Settings_Save_persists_last_cleanup()
    {
        var conn = await OpenAsync(":memory:");
        await using var _ = conn;
        await EnsureSchemaAsync(conn);

        var store = new SqliteRetentionSettingsStore(conn, new FakeClock(Now));
        var stamped = new RetentionSettings(RetentionPolicy.Default, Now);
        Assert.True((await store.SaveAsync(stamped, CancellationToken.None)).IsSuccess);

        var get = await store.GetAsync(CancellationToken.None);
        Assert.True(get.IsSuccess);
        Assert.Equal(Now, get.Value!.LastCleanupAtUtc);
    }

    [Fact]
    public async Task Settings_Save_records_injected_clock_not_system_time()
    {
        // Determinism gate (T02 / T24): updated_at_utc must come from the injected IClock, never
        // DateTimeOffset.UtcNow. With a fixed fake clock the stored value proves it.
        var conn = await OpenAsync(":memory:");
        await using var _ = conn;
        await EnsureSchemaAsync(conn);

        var store = new SqliteRetentionSettingsStore(conn, new FakeClock(Now));
        Assert.True((await store.SaveAsync(new RetentionSettings(RetentionPolicy.Default, Now), CancellationToken.None)).IsSuccess);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT updated_at_utc FROM retention_settings WHERE singleton_id = 1";
        var stored = (string?)await cmd.ExecuteScalarAsync(CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(Now.ToString("O"), stored);
    }

    // ---------------------------------------------------------------- run deletion

    [Fact]
    public async Task GetDeletableRunIds_only_terminal_finished_before_cutoff()
    {
        var conn = await OpenAsync(":memory:");
        await using var _ = conn;
        await EnsureSchemaAsync(conn);
        await SeedAutomationAsync(conn, "auto_1", "rev_1");

        var repo = new RunRepository(conn);
        // terminal + old -> eligible
        var (oldDone, snap1) = BuildRun("run_old_done", "snap_old_done", "rev_1", RunStatus.Completed, Old);
        Assert.True((await repo.CreateRunAsync(oldDone, snap1, null, CancellationToken.None)).IsSuccess);
        // terminal + recent -> NOT eligible
        var (recentDone, snap2) = BuildRun("run_recent_done", "snap_recent_done", "rev_1", RunStatus.Failed, Now);
        Assert.True((await repo.CreateRunAsync(recentDone, snap2, null, CancellationToken.None)).IsSuccess);
        // protected (waiting approval) + old -> NOT eligible
        var (waiting, snap3) = BuildRun("run_waiting", "snap_waiting", "rev_1", RunStatus.WaitingApproval, Old);
        Assert.True((await repo.CreateRunAsync(waiting, snap3, null, CancellationToken.None)).IsSuccess);

        var store = new SqliteRetentionStore(conn);
        var ids = await store.GetDeletableRunIdsAsync(Now.AddDays(-100), 100, CancellationToken.None);
        Assert.True(ids.IsSuccess);
        var list = ids.Value!.Select(x => x.ToString()).ToArray();
        Assert.Contains("run_old_done", list);
        Assert.DoesNotContain("run_recent_done", list);
        Assert.DoesNotContain("run_waiting", list);
    }

    [Fact]
    public async Task DeleteRunCascade_removes_run_steps_and_events()
    {
        var conn = await OpenAsync(":memory:");
        await using var _ = conn;
        await EnsureSchemaAsync(conn);
        await SeedAutomationAsync(conn, "auto_1", "rev_1");

        var repo = new RunRepository(conn);
        var (run, snap) = BuildRun("run_cascade", "snap_cascade", "rev_1", RunStatus.Completed, Old);
        Assert.True((await repo.CreateRunAsync(run, snap, null, CancellationToken.None)).IsSuccess);

        // add a step + an event for the run
        await using (var step = conn.CreateCommand())
        {
            step.CommandText = "INSERT INTO automation_step_runs(id,run_id,node_id,logical_execution,attempt,node_kind,status,idempotency_key,input_digest,output_summary_json,row_version) VALUES('step_1','run_cascade','n1',1,1,'agent_prompt','succeeded','idem','digest','{}',1)";
            await step.ExecuteNonQueryAsync();
        }
        await InsertEventAsync(conn, "evt_1", "run_cascade", 1, Old);

        var store = new SqliteRetentionStore(conn);
        var del = await store.DeleteRunCascadeAsync(RunId.Parse("run_cascade"), CancellationToken.None);
        Assert.True(del.IsSuccess);
        Assert.Equal(3, del.Value); // 1 step + 1 event + 1 run

        var remaining = await CountAsync(conn, "SELECT COUNT(*) FROM automation_runs WHERE id='run_cascade'");
        Assert.Equal(0, remaining);
        Assert.Equal(0, await CountAsync(conn, "SELECT COUNT(*) FROM automation_step_runs WHERE run_id='run_cascade'"));
        Assert.Equal(0, await CountAsync(conn, "SELECT COUNT(*) FROM run_events WHERE run_id='run_cascade'"));
    }

    [Fact]
    public async Task DeleteRunEventsOlderThan_only_prunes_retained_runs()
    {
        var conn = await OpenAsync(":memory:");
        await using var _ = conn;
        await EnsureSchemaAsync(conn);
        await SeedAutomationAsync(conn, "auto_1", "rev_1");

        var repo = new RunRepository(conn);
        var (keptRun, snap1) = BuildRun("run_kept", "snap_kept", "rev_1", RunStatus.Running, Now);
        Assert.True((await repo.CreateRunAsync(keptRun, snap1, null, CancellationToken.None)).IsSuccess);
        await InsertEventAsync(conn, "evt_old", "run_kept", 1, Old);
        await InsertEventAsync(conn, "evt_new", "run_kept", 2, Now);

        var store = new SqliteRetentionStore(conn);
        var del = await store.DeleteRunEventsOlderThanAsync(Now.AddDays(-100), 100, CancellationToken.None);
        Assert.True(del.IsSuccess);
        Assert.Equal(1, del.Value);
        Assert.Equal(1, await CountAsync(conn, "SELECT COUNT(*) FROM run_events WHERE run_id='run_kept'"));
    }

    [Fact]
    public async Task DeleteAuditRecordsOlderThan_removes_old_records()
    {
        var conn = await OpenAsync(":memory:");
        await using var _ = conn;
        await EnsureSchemaAsync(conn);

        await InsertAuditAsync(conn, 1, Old);
        await InsertAuditAsync(conn, 2, Now);

        var store = new SqliteRetentionStore(conn);
        var del = await store.DeleteAuditRecordsOlderThanAsync(Now.AddDays(-100), 100, CancellationToken.None);
        Assert.True(del.IsSuccess);
        Assert.Equal(1, del.Value);
        Assert.Equal(1, await CountAsync(conn, "SELECT COUNT(*) FROM security_audit_log"));
    }

    [Fact]
    public async Task DeleteResolvedIncidentsOlderThan_skips_open_incidents()
    {
        var conn = await OpenAsync(":memory:");
        await using var _ = conn;
        await EnsureSchemaAsync(conn);

        await InsertIncidentAsync(conn, "inc_resolved", (int)IncidentState.Resolved, Old);
        await InsertIncidentAsync(conn, "inc_open", (int)IncidentState.Open, Old);

        var store = new SqliteRetentionStore(conn);
        var del = await store.DeleteResolvedIncidentsOlderThanAsync(Now.AddDays(-100), 100, CancellationToken.None);
        Assert.True(del.IsSuccess);
        Assert.Equal(1, del.Value);
        Assert.Equal(1, await CountAsync(conn, "SELECT COUNT(*) FROM incidents WHERE id='inc_open'"));
        Assert.Equal(0, await CountAsync(conn, "SELECT COUNT(*) FROM incidents WHERE id='inc_resolved'"));
    }

    // ---- helpers ----

    private static async Task InsertEventAsync(SqliteConnection conn, string id, string runId, int sequence, DateTimeOffset occurred)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO run_events(id,run_id,sequence,occurred_at_utc,kind,level,step_id,attempt,code,message_key,safe_properties_json,correlation_id) VALUES($id,$run,$seq,$occ,'run_started','info',NULL,NULL,'RUN_STARTED','Run.Started','{}','corr-1')";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$run", runId);
        cmd.Parameters.AddWithValue("$seq", sequence);
        cmd.Parameters.AddWithValue("$occ", occurred.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task InsertAuditAsync(SqliteConnection conn, long sequence, DateTimeOffset occurred)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO security_audit_log(sequence,occurred_at_utc,category,action,actor,subject_json,decision_trace_json,safe_detail_json,prev_hmac,hmac,created_at_utc) VALUES($seq,$occ,0,'retention_cleanup','system','{}','{}','{}','0','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',$created)";
        cmd.Parameters.AddWithValue("$seq", sequence);
        cmd.Parameters.AddWithValue("$occ", occurred.ToString("O"));
        cmd.Parameters.AddWithValue("$created", occurred.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task InsertIncidentAsync(SqliteConnection conn, string id, int state, DateTimeOffset lastSeen)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO incidents(id,fingerprint,state,severity,type,first_seen_utc,last_seen_utc,count,recent_evidence_digests_json,resolution_code,resolution_note,resolved_at_utc,created_at_utc,updated_at_utc,last_action_id) VALUES($id,$fp,$state,1,1,$last,$last,1,'[]',NULL,NULL,NULL,$last,$last,NULL)";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$fp", Canonical);
        cmd.Parameters.AddWithValue("$state", state);
        cmd.Parameters.AddWithValue("$last", lastSeen.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<int> CountAsync(SqliteConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }
}
