using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using WorkPilot.Application.Automation.Materialization;
using WorkPilot.Application.Automation.Run;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation.Run;
using WorkPilot.Domain.Automation.Run.Materialization;
using WorkPilot.Domain.Automation.Scheduling;

namespace WorkPilot.Infrastructure.Automation;

/// <summary>
/// SQLite implementation of <see cref="IRunRepository"/> and <see cref="IMaterializationStore"/>
/// against the T07 (018) durable-run schema. Run creation is atomic (snapshot + optional occurrence +
/// run in one transaction). Event sequences are assigned under the database write lock so concurrent
/// appenders never collide (UNIQUE(run_id, sequence)). Materialization is idempotent via the
/// occurrence dedupe UNIQUE key; claim/lease operations use BEGIN IMMEDIATE + a NOT EXISTS concurrency
/// guard so at most one worker ever claims a run (RUN-002, spec doc 04 §6).
/// </summary>
public sealed class RunRepository : IRunRepository, IMaterializationStore
{
    private readonly SqliteConnection _connection;

    public RunRepository(SqliteConnection connection) => _connection = connection;

    // ---------------------------------------------------------------- IRunRepository

    public async Task<Result> CreateRunAsync(AutomationRun run, RunSnapshot snapshot, TriggerOccurrence? occurrence, CancellationToken ct)
    {
        await using var transaction = await _connection.BeginTransactionAsync(ct);
        try
        {
            await InsertSnapshotAsync(transaction, snapshot, ct);
            if (occurrence is not null)
                await InsertOccurrenceAsync(transaction, occurrence, ct);
            await InsertRunAsync(transaction, run, ct);
            await transaction.CommitAsync(ct);
            return Result.Success();
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            await transaction.RollbackAsync(ct);
            return Result.Failure(MapStoreError(ex));
        }
    }

    public async Task<Result<RunWithDetails?>> GetRunAsync(RunId id, CancellationToken ct)
    {
        var run = await LoadRunAsync(id, ct);
        if (run is null)
            return Result<RunWithDetails?>.Ok(null);

        var snapshot = await LoadSnapshotAsync(run.SnapshotId, ct);
        var steps = await LoadStepsAsync(id, ct);
        var events = await LoadEventsAsync(id, ct);
        return Result<RunWithDetails?>.Ok(new RunWithDetails(run, snapshot!, steps, events));
    }

    public async Task<Result> AppendEventAsync(RunEvent ev, CancellationToken ct)
    {
        await using var transaction = await _connection.BeginTransactionAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.Transaction = (SqliteTransaction)transaction;
            cmd.CommandText = """
                INSERT INTO run_events(id,run_id,sequence,occurred_at_utc,kind,level,code,message_key,safe_properties_json,correlation_id,step_id,attempt)
                SELECT $id,$run, COALESCE((SELECT MAX(sequence) FROM run_events WHERE run_id=$run),0)+1,
                       $occ,$kind,$level,$code,$msg,$props,$corr,$step,$attempt
                """;
            BindEventParameters(cmd, ev);
            await cmd.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
            return Result.Success();
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            await transaction.RollbackAsync(ct);
            return Result.Failure(MapStoreError(ex));
        }
    }

    public async Task<Result> AppendEventsAsync(IReadOnlyList<RunEvent> events, CancellationToken ct)
    {
        if (events.Count == 0)
            return Result.Success();

        // Group by run so each run's sequence stays contiguous and unique (UNIQUE(run_id, sequence)).
        var byRun = events.GroupBy(e => e.RunId).ToList();
        await using var transaction = await _connection.BeginTransactionAsync(ct);
        try
        {
            foreach (var group in byRun)
            {
                var currentMax = Convert.ToInt32(await ScalarMaxSequenceAsync(transaction, group.Key, ct));
                foreach (var ev in group)
                {
                    var stamped = ev.WithSequence(++currentMax);
                    var cmd = _connection.CreateCommand();
                    cmd.Transaction = (SqliteTransaction)transaction;
                    cmd.CommandText = """
                        INSERT INTO run_events(id,run_id,sequence,occurred_at_utc,kind,level,code,message_key,safe_properties_json,correlation_id,step_id,attempt)
                        VALUES($id,$run,$seq,$occ,$kind,$level,$code,$msg,$props,$corr,$step,$attempt)
                        """;
                    BindEventParameters(cmd, stamped);
                    cmd.Parameters.AddWithValue("$seq", stamped.Sequence);
                    await cmd.ExecuteNonQueryAsync(ct);
                }
            }
            await transaction.CommitAsync(ct);
            return Result.Success();
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            await transaction.RollbackAsync(ct);
            return Result.Failure(MapStoreError(ex));
        }
    }

    public async Task<Result<RunListPage>> ListRunsAsync(RunQuery query, CancellationToken ct)
    {
        var sql = new System.Text.StringBuilder("""
            SELECT id,automation_id,automation_revision_id,trigger_kind,status,priority,scheduled_at_utc,started_at_utc,finished_at_utc,final_error_code
            FROM automation_runs
            """);
        var conditions = new List<string>();
        var cmd = _connection.CreateCommand();

        if (query.AutomationId is not null)
        {
            conditions.Add("automation_id=$aid");
            cmd.Parameters.AddWithValue("$aid", query.AutomationId.Value.Value);
        }
        if (query.Status is not null)
        {
            conditions.Add("status=$status");
            cmd.Parameters.AddWithValue("$status", query.Status.Value.ToStorage());
        }
        if (query.TriggerKind is not null)
        {
            conditions.Add("trigger_kind=$tk");
            cmd.Parameters.AddWithValue("$tk", query.TriggerKind.Value.ToStorage());
        }
        if (query.FromUtc is not null)
        {
            conditions.Add("started_at_utc >= $from");
            cmd.Parameters.AddWithValue("$from", query.FromUtc.Value.ToString("O"));
        }
        if (query.ToUtc is not null)
        {
            conditions.Add("started_at_utc <= $to");
            cmd.Parameters.AddWithValue("$to", query.ToUtc.Value.ToString("O"));
        }
        if (query.Cursor is not null)
        {
            conditions.Add("($curStarted IS NULL OR started_at_utc < $curStarted OR (started_at_utc = $curStarted AND id < $curId))");
            cmd.Parameters.AddWithValue("$curStarted", (object?)query.Cursor.StartedAtUtc?.ToString("O") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$curId", query.Cursor.Id.Value);
        }

        if (conditions.Count > 0)
            sql.Append(" WHERE ").Append(string.Join(" AND ", conditions));
        sql.Append(" ORDER BY started_at_utc DESC, id DESC LIMIT ").Append(query.PageSize + 1);

        cmd.CommandText = sql.ToString();
        var items = new List<RunListItem>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                items.Add(MapListItem(reader));
        }

        var hasMore = items.Count > query.PageSize;
        if (hasMore)
            items.RemoveAt(items.Count - 1);

        RunListCursor? next = null;
        if (hasMore && items.Count > 0)
        {
            var last = items[^1];
            next = new RunListCursor(last.StartedAtUtc, last.Id);
        }

        return Result<RunListPage>.Ok(new RunListPage(items, hasMore, next));
    }

    public async Task<Result<bool>> TryClaimAsync(RunId id, string owner, DateTimeOffset leaseExpiresAt, CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE automation_runs
            SET status='claimed', lease_owner=$owner, lease_expires_at_utc=$exp, claimed_at_utc=$now, row_version=row_version+1
            WHERE id=$id AND status='queued'
            """;
        cmd.Parameters.AddWithValue("$id", id.Value);
        cmd.Parameters.AddWithValue("$owner", owner);
        cmd.Parameters.AddWithValue("$exp", leaseExpiresAt.ToString("O"));
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        var affected = await cmd.ExecuteNonQueryAsync(ct);
        return Result<bool>.Ok(affected == 1);
    }

    public async Task<Result> RequestCancellationAsync(RunId id, DateTimeOffset now, CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE automation_runs
            SET cancellation_requested_at_utc=COALESCE(cancellation_requested_at_utc,$now), row_version=row_version+1
            WHERE id=$id
            """;
        cmd.Parameters.AddWithValue("$id", id.Value);
        cmd.Parameters.AddWithValue("$now", now.ToString("O"));
        return await MaybeNotFoundAsync(cmd, ct);
    }

    public async Task<Result> CancelAsync(RunId id, DateTimeOffset now, CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE automation_runs
            SET status='cancelled', finished_at_utc=COALESCE(finished_at_utc,$now),
                cancellation_requested_at_utc=COALESCE(cancellation_requested_at_utc,$now), row_version=row_version+1
            WHERE id=$id
            """;
        cmd.Parameters.AddWithValue("$id", id.Value);
        cmd.Parameters.AddWithValue("$now", now.ToString("O"));
        return await MaybeNotFoundAsync(cmd, ct);
    }

    public async Task<Result> DeleteRunAsync(RunId id, CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM automation_runs WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id.Value);
        var affected = await cmd.ExecuteNonQueryAsync(ct);
        return affected == 0 ? Result.Failure(RunErrors.NotFoundError()) : Result.Success();
    }

    public async Task<Result> UpsertStepAsync(StepRun step, CancellationToken ct)
    {
        try
        {
            await using var tx = await _connection.BeginTransactionAsync(ct);
            await UpsertStepInternalAsync((SqliteTransaction)tx, step, ct);
            await tx.CommitAsync(ct);
            return Result.Success();
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            return Result.Failure(MapStoreError(ex));
        }
    }

    public async Task<Result> PersistExecutionResultAsync(
        AutomationRun run,
        IReadOnlyList<StepRun> steps,
        IReadOnlyList<RunEvent> events,
        CancellationToken ct)
    {
        await using var transaction = await _connection.BeginTransactionAsync(ct);
        try
        {
            var tx = (SqliteTransaction)transaction;
            await UpdateRunHeaderAsync(tx, run, ct);
            foreach (var step in steps)
                await UpsertStepInternalAsync(tx, step, ct);
            // Assign contiguous per-run sequences under the transaction's write lock so appended
            // events never collide with UNIQUE(run_id, sequence).
            var sequence = await ScalarMaxSequenceAsync(tx, run.Id, ct);
            foreach (var ev in events)
            {
                sequence++;
                await InsertRunEventWithSequenceAsync(tx, ev, sequence, ct);
            }
            await transaction.CommitAsync(ct);
            return Result.Success();
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            await transaction.RollbackAsync(ct);
            return Result.Failure(MapStoreError(ex));
        }
    }

    private async Task UpsertStepInternalAsync(SqliteTransaction tx, StepRun s, CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO automation_step_runs(id,run_id,node_id,logical_execution,attempt,node_kind,status,side_effect_phase,
              idempotency_key,input_digest,output_summary_json,resume_at_utc,started_at_utc,finished_at_utc,duration_ms,error_code,row_version)
            VALUES($id,$run,$node,$logic,$attempt,$kind,$status,$phase,$idem,$digest,$out,$resume,$started,$finished,$dur,$err,$rv)
            ON CONFLICT(id) DO UPDATE SET
              status=excluded.status, side_effect_phase=excluded.side_effect_phase, output_summary_json=excluded.output_summary_json,
              resume_at_utc=excluded.resume_at_utc, started_at_utc=excluded.started_at_utc, finished_at_utc=excluded.finished_at_utc,
              duration_ms=excluded.duration_ms, error_code=excluded.error_code, row_version=excluded.row_version
            """;
        cmd.Parameters.AddWithValue("$id", s.Id.Value);
        cmd.Parameters.AddWithValue("$run", s.RunId.Value);
        cmd.Parameters.AddWithValue("$node", s.NodeId);
        cmd.Parameters.AddWithValue("$logic", s.LogicalExecution);
        cmd.Parameters.AddWithValue("$attempt", s.Attempt);
        cmd.Parameters.AddWithValue("$kind", s.NodeKind);
        cmd.Parameters.AddWithValue("$status", s.Status.ToStorage());
        cmd.Parameters.AddWithValue("$phase", (object?)s.SideEffectPhase.ToStorage() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$idem", s.IdempotencyKey);
        cmd.Parameters.AddWithValue("$digest", s.InputDigest);
        cmd.Parameters.AddWithValue("$out", (object?)s.OutputSummaryJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$resume", (object?)s.ResumeAtUtc?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$started", (object?)s.StartedAtUtc?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$finished", (object?)s.FinishedAtUtc?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dur", s.DurationMs);
        cmd.Parameters.AddWithValue("$err", (object?)s.ErrorCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rv", s.RowVersion);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task UpdateRunHeaderAsync(SqliteTransaction tx, AutomationRun r, CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE automation_runs SET
              status=$status, started_at_utc=$started, finished_at_utc=$finished, current_node_id=$curnode,
              last_event_sequence=$lastEv, active_duration_ms=$act, model_turn_count=$mt, capability_call_count=$capc,
              result_bytes=$res, final_error_code=$err, row_version=$rv
            WHERE id=$id
            """;
        cmd.Parameters.AddWithValue("$id", r.Id.Value);
        cmd.Parameters.AddWithValue("$status", r.Status.ToStorage());
        cmd.Parameters.AddWithValue("$started", (object?)r.StartedAtUtc?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$finished", (object?)r.FinishedAtUtc?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$curnode", (object?)r.CurrentNodeId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lastEv", r.LastEventSequence);
        cmd.Parameters.AddWithValue("$act", r.ActiveDurationMs);
        cmd.Parameters.AddWithValue("$mt", r.ModelTurnCount);
        cmd.Parameters.AddWithValue("$capc", r.CapabilityCallCount);
        cmd.Parameters.AddWithValue("$res", r.ResultBytes);
        cmd.Parameters.AddWithValue("$err", (object?)r.FinalErrorCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rv", r.RowVersion);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task InsertRunEventWithSequenceAsync(SqliteTransaction tx, RunEvent ev, int sequence, CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO run_events(id,run_id,sequence,occurred_at_utc,kind,level,code,message_key,safe_properties_json,correlation_id,step_id,attempt)
            VALUES($id,$run,$seq,$occ,$kind,$level,$code,$msg,$props,$corr,$step,$attempt)
            """;
        BindEventParameters(cmd, ev);
        cmd.Parameters.AddWithValue("$seq", sequence);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---------------------------------------------------------------- IMaterializationStore

    public async Task<Result<bool>> TryReserveOccurrenceAsync(TriggerOccurrence occurrence, CancellationToken ct)
    {
        await using var transaction = await _connection.BeginTransactionAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.Transaction = (SqliteTransaction)transaction;
            cmd.CommandText = """
                INSERT INTO automation_trigger_occurrences(id,automation_id,automation_revision_id,trigger_id,scheduled_at_utc,materialized_at_utc,disposition,dedupe_key,missed_count,safe_trigger_json)
                VALUES($id,$aid,$rev,$tid,$sched,$mat,$disp,$dedupe,$missed,$safe)
                """;
            cmd.Parameters.AddWithValue("$id", occurrence.Id.Value);
            cmd.Parameters.AddWithValue("$aid", occurrence.AutomationId.Value);
            cmd.Parameters.AddWithValue("$rev", occurrence.AutomationRevisionId.Value);
            cmd.Parameters.AddWithValue("$tid", occurrence.TriggerId);
            cmd.Parameters.AddWithValue("$sched", occurrence.ScheduledAtUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$mat", occurrence.MaterializedAtUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$disp", occurrence.Disposition.ToStorage());
            cmd.Parameters.AddWithValue("$dedupe", occurrence.DedupeKey);
            cmd.Parameters.AddWithValue("$missed", occurrence.MissedCount);
            cmd.Parameters.AddWithValue("$safe", occurrence.SafeTriggerJson);
            await cmd.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
            return Result<bool>.Ok(true);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 19 or 2067 || ex.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase))
        {
            await transaction.RollbackAsync(ct);
            return Result<bool>.Ok(false); // duplicate dedupe key => already materialized
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            await transaction.RollbackAsync(ct);
            return Result<bool>.Fail(MapStoreError(ex));
        }
    }

    public async Task<Result> CreateRunForOccurrenceAsync(AutomationRun run, RunSnapshot snapshot, RunEvent createdEvent, CancellationToken ct)
    {
        await using var transaction = await _connection.BeginTransactionAsync(ct);
        try
        {
            await InsertSnapshotAsync(transaction, snapshot, ct);
            await InsertRunAsync(transaction, run, ct);
            await InsertRunEventAsync(transaction, createdEvent, ct);
            await transaction.CommitAsync(ct);
            return Result.Success();
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            await transaction.RollbackAsync(ct);
            return Result.Failure(MapStoreError(ex));
        }
    }

    public async Task<Result> RecordCoalesceAsync(RunId targetRunId, int coalescedCount, TriggerOccurrence occurrence, RunEvent coalescedEvent, CancellationToken ct)
    {
        // The occurrence was already reserved by TryReserveOccurrenceAsync (idempotent dedupe) before
        // the overlap decision, so we only bump the target run's coalesced_count and append the event.
        await using var transaction = await _connection.BeginTransactionAsync(ct);
        try
        {
            var upd = _connection.CreateCommand();
            upd.Transaction = (SqliteTransaction)transaction;
            upd.CommandText = "UPDATE automation_runs SET coalesced_count=$c, row_version=row_version+1 WHERE id=$tid";
            upd.Parameters.AddWithValue("$c", coalescedCount);
            upd.Parameters.AddWithValue("$tid", targetRunId.Value);
            await upd.ExecuteNonQueryAsync(ct);

            await InsertRunEventAsync(transaction, coalescedEvent, ct);
            await transaction.CommitAsync(ct);
            return Result.Success();
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            await transaction.RollbackAsync(ct);
            return Result.Failure(MapStoreError(ex));
        }
    }

    public async Task<Result<IReadOnlyList<ExistingRunSummary>>> GetActiveRunsAsync(AutomationId automationId, CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, status, (cancellation_requested_at_utc IS NOT NULL) AS canc
            FROM automation_runs
            WHERE automation_id=$aid
              AND status NOT IN ('completed','failed','cancelled','blocked_policy','needs_review')
            """;
        cmd.Parameters.AddWithValue("$aid", automationId.Value);
        var list = new List<ExistingRunSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var status = RunStorageMaps.StatusFromStorage(reader.GetString(1));
            list.Add(new ExistingRunSummary(RunId.Parse(reader.GetString(0)), status.ToCategory(), reader.GetBoolean(2)));
        }
        return Result<IReadOnlyList<ExistingRunSummary>>.Ok(list);
    }

    public async Task<Result<IReadOnlyList<QueuedRunInfo>>> GetClaimableQueuedAsync(DateTimeOffset now, int batchSize, CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, automation_id, priority, scheduled_at_utc, available_at_utc
            FROM automation_runs
            WHERE status='queued' AND available_at_utc <= $now AND cancellation_requested_at_utc IS NULL
            ORDER BY priority DESC, scheduled_at_utc ASC, id ASC
            LIMIT $batch
            """;
        cmd.Parameters.AddWithValue("$now", now.ToString("O"));
        cmd.Parameters.AddWithValue("$batch", batchSize);
        var list = new List<QueuedRunInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new QueuedRunInfo(
                RunId.Parse(reader.GetString(0)),
                AutomationId.Parse(reader.GetString(1)),
                reader.GetInt32(2),
                ReadDto(reader, 3),
                ReadDto(reader, 4)));
        }
        return Result<IReadOnlyList<QueuedRunInfo>>.Ok(list);
    }

    public async Task<Result<IReadOnlyList<RunId>>> ClaimBatchAsync(IReadOnlyList<RunId> ids, string owner, DateTimeOffset leaseExpiresAt, DateTimeOffset now, CancellationToken ct)
    {
        if (ids.Count == 0)
            return Result<IReadOnlyList<RunId>>.Ok(Array.Empty<RunId>());

        await using var transaction = await _connection.BeginTransactionAsync(ct);
        try
        {
            var (inClause, bind) = BuildInClause(ids);
            // Only claim ids that are still queued, available, not cancelled, and whose automation has
            // no other active (claimed/running/waiting) execution — enforces per-automation concurrency 1.
            var select = _connection.CreateCommand();
            select.Transaction = (SqliteTransaction)transaction;
            select.CommandText = $"""
                SELECT id FROM automation_runs
                WHERE id IN ({inClause}) AND status='queued' AND available_at_utc <= $now AND cancellation_requested_at_utc IS NULL
                  AND NOT EXISTS (
                    SELECT 1 FROM automation_runs r2
                    WHERE r2.automation_id = automation_runs.automation_id
                      AND r2.status IN ('claimed','running','waiting_delay','waiting_approval'))
                ORDER BY priority DESC, scheduled_at_utc ASC, id ASC
                """;
            select.Parameters.AddWithValue("$now", now.ToString("O"));
            bind(select);
            var eligible = new List<string>();
            await using (var reader = await select.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct))
                    eligible.Add(reader.GetString(0));

            if (eligible.Count > 0)
            {
                var (inClause2, bind2) = BuildInClause(eligible.Select(RunId.Parse).ToList());
                var upd = _connection.CreateCommand();
                upd.Transaction = (SqliteTransaction)transaction;
                upd.CommandText = $"""
                    UPDATE automation_runs
                    SET status='claimed', lease_owner=$owner, lease_expires_at_utc=$exp, claimed_at_utc=$now, row_version=row_version+1
                    WHERE id IN ({inClause2}) AND status='queued'
                    """;
                upd.Parameters.AddWithValue("$owner", owner);
                upd.Parameters.AddWithValue("$exp", leaseExpiresAt.ToString("O"));
                upd.Parameters.AddWithValue("$now", now.ToString("O"));
                bind2(upd);
                await upd.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
            return Result<IReadOnlyList<RunId>>.Ok(eligible.Select(RunId.Parse).ToList());
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            await transaction.RollbackAsync(ct);
            return Result<IReadOnlyList<RunId>>.Ok(Array.Empty<RunId>());
        }
    }

    public async Task<Result> HeartbeatAsync(string owner, DateTimeOffset leaseExpiresAt, IReadOnlyList<RunId> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
            return Result.Success();
        var (inClause, bind) = BuildInClause(ids);
        var cmd = _connection.CreateCommand();
        cmd.CommandText = $"""
            UPDATE automation_runs
            SET lease_expires_at_utc=$exp, row_version=row_version+1
            WHERE id IN ({inClause}) AND lease_owner=$owner AND status IN ('claimed','running')
            """;
        cmd.Parameters.AddWithValue("$exp", leaseExpiresAt.ToString("O"));
        cmd.Parameters.AddWithValue("$owner", owner);
        bind(cmd);
        await cmd.ExecuteNonQueryAsync(ct);
        return Result.Success();
    }

    public async Task<Result> ReleaseLeaseAsync(RunId runId, CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE automation_runs
            SET status='queued', lease_owner=NULL, lease_expires_at_utc=NULL, claimed_at_utc=NULL, row_version=row_version+1
            WHERE id=$id AND status='claimed'
            """;
        cmd.Parameters.AddWithValue("$id", runId.Value);
        return await MaybeNotFoundAsync(cmd, ct);
    }

    public async Task<Result<IReadOnlyList<ExpiredLease>>> ScanExpiredLeasesAsync(DateTimeOffset now, int batchSize, CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT r.id, r.status, r.recovery_count,
                   (SELECT COUNT(*) FROM automation_step_runs s
                     WHERE s.run_id=r.id AND s.finished_at_utc IS NULL
                       AND s.side_effect_phase IN ('permit_issued','request_sending','response_received')) AS side_effect
            FROM automation_runs r
            WHERE r.lease_expires_at_utc IS NOT NULL AND r.lease_expires_at_utc < $now
              AND r.status IN ('claimed','running')
            ORDER BY r.lease_expires_at_utc ASC
            LIMIT $batch
            """;
        cmd.Parameters.AddWithValue("$now", now.ToString("O"));
        cmd.Parameters.AddWithValue("$batch", batchSize);
        var list = new List<ExpiredLease>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var status = RunStorageMaps.StatusFromStorage(reader.GetString(1));
            list.Add(new ExpiredLease(
                RunId.Parse(reader.GetString(0)),
                status,
                reader.GetInt32(3) > 0,
                reader.GetInt32(2)));
        }
        return Result<IReadOnlyList<ExpiredLease>>.Ok(list);
    }

    public async Task<Result> RecoverLeaseAsync(RunId runId, DateTimeOffset now, bool sideEffectInFlight, CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE automation_runs
            SET status = CASE
                    WHEN $sideEffect = 1 THEN 'needs_review'
                    WHEN recovery_count >= 3 THEN 'failed'
                    ELSE 'queued' END,
                finished_at_utc = CASE
                    WHEN $sideEffect = 1 OR recovery_count >= 3 THEN COALESCE(finished_at_utc, $now)
                    ELSE finished_at_utc END,
                final_error_code = CASE WHEN recovery_count >= 3 AND $sideEffect = 0 THEN 'repeated_worker_crash' ELSE final_error_code END,
                lease_owner = NULL,
                lease_expires_at_utc = NULL,
                claimed_at_utc = NULL,
                recovery_count = CASE WHEN $sideEffect = 1 OR recovery_count >= 3 THEN recovery_count ELSE recovery_count + 1 END,
                row_version = row_version + 1
            WHERE id=$id AND status IN ('claimed','running')
            """;
        cmd.Parameters.AddWithValue("$sideEffect", sideEffectInFlight ? 1 : 0);
        cmd.Parameters.AddWithValue("$now", now.ToString("O"));
        cmd.Parameters.AddWithValue("$id", runId.Value);
        return await MaybeNotFoundAsync(cmd, ct);
    }

    // ---------------------------------------------------------------- internals

    private const string RunSelectSql = """
        SELECT id,automation_id,automation_revision_id,occurrence_id,snapshot_id,parent_run_id,trigger_kind,status,priority,
               scheduled_at_utc,available_at_utc,claimed_at_utc,started_at_utc,finished_at_utc,lease_owner,lease_expires_at_utc,
               cancellation_requested_at_utc,current_node_id,last_event_sequence,active_duration_ms,model_turn_count,
               capability_call_count,result_bytes,coalesced_count,recovery_count,final_error_code,row_version
        FROM automation_runs WHERE id=$id
        """;

    private static (string clause, Action<SqliteCommand> binder) BuildInClause(IReadOnlyList<RunId> ids)
    {
        var parts = new List<string>();
        var parameters = new List<(string, string)>();
        for (var i = 0; i < ids.Count; i++)
        {
            var p = "$in" + i;
            parts.Add(p);
            parameters.Add((p, ids[i].Value));
        }
        return (string.Join(",", parts), cmd =>
        {
            foreach (var (p, v) in parameters)
                cmd.Parameters.AddWithValue(p, v);
        });
    }

    private async Task<AutomationRun?> LoadRunAsync(RunId id, CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = RunSelectSql;
        cmd.Parameters.AddWithValue("$id", id.Value);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapRun(reader) : null;
    }

    private async Task<RunSnapshot?> LoadSnapshotAsync(RunSnapshotId id, CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id,automation_revision_id,expert_revision_id,policy_snapshot_json,capability_snapshot_json,
                   workflow_snapshot_json,binding_snapshot_json,budget_snapshot_json,revocation_epoch,algorithm_versions_json,canonical_sha256,created_at_utc
            FROM automation_run_snapshots WHERE id=$id
            """;
        cmd.Parameters.AddWithValue("$id", id.Value);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapSnapshot(reader) : null;
    }

    private async Task<IReadOnlyList<StepRun>> LoadStepsAsync(RunId runId, CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id,run_id,node_id,logical_execution,attempt,node_kind,status,side_effect_phase,idempotency_key,input_digest,
                   output_summary_json,resume_at_utc,started_at_utc,finished_at_utc,duration_ms,error_code,row_version
            FROM automation_step_runs WHERE run_id=$run ORDER BY logical_execution, attempt
            """;
        cmd.Parameters.AddWithValue("$run", runId.Value);
        var steps = new List<StepRun>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            steps.Add(MapStep(reader));
        return steps;
    }

    private async Task<IReadOnlyList<RunEvent>> LoadEventsAsync(RunId runId, CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id,run_id,sequence,occurred_at_utc,kind,level,code,message_key,safe_properties_json,correlation_id,step_id,attempt
            FROM run_events WHERE run_id=$run ORDER BY sequence
            """;
        cmd.Parameters.AddWithValue("$run", runId.Value);
        var events = new List<RunEvent>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            events.Add(MapEvent(reader));
        return events;
    }

    private async Task InsertSnapshotAsync(DbTransaction tx, RunSnapshot s, CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText = """
            INSERT INTO automation_run_snapshots(id,automation_revision_id,expert_revision_id,policy_snapshot_json,capability_snapshot_json,
              workflow_snapshot_json,binding_snapshot_json,budget_snapshot_json,revocation_epoch,algorithm_versions_json,canonical_sha256,created_at_utc)
            VALUES($id,$rev,$exp,$policy,$cap,$wf,$bind,$budget,$epoch,$algo,$canon,$created)
            """;
        cmd.Parameters.AddWithValue("$id", s.Id.Value);
        cmd.Parameters.AddWithValue("$rev", s.AutomationRevisionId.Value);
        cmd.Parameters.AddWithValue("$exp", s.ExpertRevisionId.Value);
        cmd.Parameters.AddWithValue("$policy", s.PolicySnapshotJson);
        cmd.Parameters.AddWithValue("$cap", s.CapabilitySnapshotJson);
        cmd.Parameters.AddWithValue("$wf", s.WorkflowSnapshotJson);
        cmd.Parameters.AddWithValue("$bind", s.BindingSnapshotJson);
        cmd.Parameters.AddWithValue("$budget", s.BudgetSnapshotJson);
        cmd.Parameters.AddWithValue("$epoch", s.RevocationEpoch);
        cmd.Parameters.AddWithValue("$algo", s.AlgorithmVersionsJson);
        cmd.Parameters.AddWithValue("$canon", s.CanonicalSha256);
        cmd.Parameters.AddWithValue("$created", s.CreatedAtUtc.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task InsertOccurrenceAsync(DbTransaction tx, TriggerOccurrence o, CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText = """
            INSERT INTO automation_trigger_occurrences(id,automation_id,automation_revision_id,trigger_id,scheduled_at_utc,materialized_at_utc,disposition,dedupe_key,missed_count,safe_trigger_json)
            VALUES($id,$aid,$rev,$tid,$sched,$mat,$disp,$dedupe,$missed,$safe)
            """;
        cmd.Parameters.AddWithValue("$id", o.Id.Value);
        cmd.Parameters.AddWithValue("$aid", o.AutomationId.Value);
        cmd.Parameters.AddWithValue("$rev", o.AutomationRevisionId.Value);
        cmd.Parameters.AddWithValue("$tid", o.TriggerId);
        cmd.Parameters.AddWithValue("$sched", o.ScheduledAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$mat", o.MaterializedAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$disp", o.Disposition.ToStorage());
        cmd.Parameters.AddWithValue("$dedupe", o.DedupeKey);
        cmd.Parameters.AddWithValue("$missed", o.MissedCount);
        cmd.Parameters.AddWithValue("$safe", o.SafeTriggerJson);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task InsertRunAsync(DbTransaction tx, AutomationRun r, CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText = """
            INSERT INTO automation_runs(id,automation_id,automation_revision_id,occurrence_id,snapshot_id,parent_run_id,trigger_kind,status,priority,
              scheduled_at_utc,available_at_utc,claimed_at_utc,started_at_utc,finished_at_utc,lease_owner,lease_expires_at_utc,
              cancellation_requested_at_utc,current_node_id,last_event_sequence,active_duration_ms,model_turn_count,capability_call_count,
              result_bytes,coalesced_count,recovery_count,final_error_code,row_version)
            VALUES($id,$aid,$rev,$occ,$snap,$parent,$tk,$status,$pri,$sched,$avail,$claimed,$started,$finished,$owner,$lexp,$creq,$curnode,
              $lastEv,$act,$mt,$capc,$res,$coal,$rec,$err,$rv)
            """;
        cmd.Parameters.AddWithValue("$id", r.Id.Value);
        cmd.Parameters.AddWithValue("$aid", (object?)r.AutomationId?.Value ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rev", r.AutomationRevisionId.Value);
        cmd.Parameters.AddWithValue("$occ", (object?)r.OccurrenceId?.Value ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$snap", r.SnapshotId.Value);
        cmd.Parameters.AddWithValue("$parent", (object?)r.ParentRunId?.Value ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tk", r.TriggerKind.ToStorage());
        cmd.Parameters.AddWithValue("$status", r.Status.ToStorage());
        cmd.Parameters.AddWithValue("$pri", r.Priority);
        cmd.Parameters.AddWithValue("$sched", r.ScheduledAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$avail", r.AvailableAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$claimed", (object?)r.ClaimedAtUtc?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$started", (object?)r.StartedAtUtc?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$finished", (object?)r.FinishedAtUtc?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$owner", (object?)r.LeaseOwner ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lexp", (object?)r.LeaseExpiresAtUtc?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$creq", (object?)r.CancellationRequestedAtUtc?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$curnode", (object?)r.CurrentNodeId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lastEv", r.LastEventSequence);
        cmd.Parameters.AddWithValue("$act", r.ActiveDurationMs);
        cmd.Parameters.AddWithValue("$mt", r.ModelTurnCount);
        cmd.Parameters.AddWithValue("$capc", r.CapabilityCallCount);
        cmd.Parameters.AddWithValue("$res", r.ResultBytes);
        cmd.Parameters.AddWithValue("$coal", r.CoalescedCount);
        cmd.Parameters.AddWithValue("$rec", r.RecoveryCount);
        cmd.Parameters.AddWithValue("$err", (object?)r.FinalErrorCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rv", r.RowVersion);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task InsertRunEventAsync(DbTransaction tx, RunEvent ev, CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText = """
            INSERT INTO run_events(id,run_id,sequence,occurred_at_utc,kind,level,code,message_key,safe_properties_json,correlation_id,step_id,attempt)
            SELECT $id,$run, COALESCE((SELECT MAX(sequence) FROM run_events WHERE run_id=$run),0)+1,
                   $occ,$kind,$level,$code,$msg,$props,$corr,$step,$attempt
            """;
        BindEventParameters(cmd, ev);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<int> ScalarMaxSequenceAsync(DbTransaction tx, RunId runId, CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText = "SELECT COALESCE((SELECT MAX(sequence) FROM run_events WHERE run_id=$run),0)";
        cmd.Parameters.AddWithValue("$run", runId.Value);
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is DBNull ? 0 : Convert.ToInt32(v ?? 0);
    }

    private static void BindEventParameters(SqliteCommand cmd, RunEvent ev)
    {
        cmd.Parameters.AddWithValue("$id", ev.Id.Value);
        cmd.Parameters.AddWithValue("$run", ev.RunId.Value);
        cmd.Parameters.AddWithValue("$occ", ev.OccurredAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$kind", ev.Kind);
        cmd.Parameters.AddWithValue("$level", ev.Level.ToStorage());
        cmd.Parameters.AddWithValue("$code", ev.Code);
        cmd.Parameters.AddWithValue("$msg", ev.MessageKey);
        cmd.Parameters.AddWithValue("$props", ev.SafePropertiesJson);
        cmd.Parameters.AddWithValue("$corr", ev.CorrelationId);
        cmd.Parameters.AddWithValue("$step", (object?)ev.StepId?.Value ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$attempt", (object?)ev.Attempt ?? DBNull.Value);
    }

    private static async Task<Result> MaybeNotFoundAsync(SqliteCommand cmd, CancellationToken ct)
    {
        var affected = await cmd.ExecuteNonQueryAsync(ct);
        return affected == 0 ? Result.Failure(RunErrors.NotFoundError()) : Result.Success();
    }

    private static AppError MapStoreError(Exception ex)
        => new AppError("RUN_STORE", ErrorCategory.Database, "Run.StoreError", false,
            new Dictionary<string, string> { ["detail"] = ex.Message });

    private static AutomationRun MapRun(DbDataReader r)
    {
        return new AutomationRun(
            RunId.Parse(r.GetString(0)),
            AutomationRevisionId.Parse(r.GetString(2)),
            RunSnapshotId.Parse(r.GetString(4)),
            RunStorageMaps.TriggerKindFromStorage(r.GetString(6)),
            RunStorageMaps.StatusFromStorage(r.GetString(7)),
            r.GetInt32(8),
            ReadDto(r, 9),
            ReadDto(r, 10),
            r.IsDBNull(1) ? null : AutomationId.Parse(r.GetString(1)),
            r.IsDBNull(3) ? null : TriggerOccurrenceId.Parse(r.GetString(3)),
            r.IsDBNull(5) ? null : RunId.Parse(r.GetString(5)),
            ReadDtoN(r, 11),
            ReadDtoN(r, 12),
            ReadDtoN(r, 13),
            r.IsDBNull(14) ? null : r.GetString(14),
            ReadDtoN(r, 15),
            ReadDtoN(r, 16),
            r.IsDBNull(17) ? null : r.GetString(17),
            null, // ResumeAtUtc: in-memory only (no automation_runs column; resume time lives on the waiting step)
            r.GetInt32(18),
            r.GetInt32(19),
            r.GetInt32(20),
            r.GetInt32(21),
            r.GetInt32(22),
            r.GetInt32(23),
            r.GetInt32(24),
            r.IsDBNull(25) ? null : r.GetString(25),
            r.GetInt32(26));
    }

    private static RunSnapshot MapSnapshot(DbDataReader r)
    {
        return new RunSnapshot(
            RunSnapshotId.Parse(r.GetString(0)),
            AutomationRevisionId.Parse(r.GetString(1)),
            ExpertRevisionId.Parse(r.GetString(2)),
            r.GetString(3),
            r.GetString(4),
            r.GetString(5),
            r.GetString(6),
            r.GetString(7),
            r.GetInt32(8),
            r.GetString(9),
            r.GetString(10),
            ReadDto(r, 11));
    }

    private static StepRun MapStep(DbDataReader r)
    {
        return new StepRun(
            StepRunId.Parse(r.GetString(0)),
            RunId.Parse(r.GetString(1)),
            r.GetString(2),
            r.GetInt32(3),
            r.GetInt32(4),
            r.GetString(5),
            RunStorageMaps.StepStatusFromStorage(r.GetString(6)),
            RunStorageMaps.SideEffectPhaseFromStorage(r.IsDBNull(7) ? null : r.GetString(7)),
            r.GetString(8),
            r.GetString(9),
            r.IsDBNull(10) ? null : r.GetString(10),
            ReadDtoN(r, 11),
            ReadDtoN(r, 12),
            ReadDtoN(r, 13),
            r.GetInt32(14),
            r.IsDBNull(15) ? null : r.GetString(15),
            r.GetInt32(16));
    }

    private static RunEvent MapEvent(DbDataReader r)
    {
        return new RunEvent(
            RunEventId.Parse(r.GetString(0)),
            RunId.Parse(r.GetString(1)),
            r.GetInt32(2),
            ReadDto(r, 3),
            r.GetString(4),
            RunStorageMaps.EventLevelFromStorage(r.GetString(5)),
            r.GetString(6),
            r.GetString(7),
            r.GetString(8),
            r.GetString(9),
            r.IsDBNull(10) ? null : StepRunId.Parse(r.GetString(10)),
            r.IsDBNull(11) ? null : r.GetInt32(11));
    }

    private static RunListItem MapListItem(DbDataReader r)
    {
        return new RunListItem(
            RunId.Parse(r.GetString(0)),
            r.IsDBNull(1) ? null : AutomationId.Parse(r.GetString(1)),
            AutomationRevisionId.Parse(r.GetString(2)),
            RunStorageMaps.TriggerKindFromStorage(r.GetString(3)),
            RunStorageMaps.StatusFromStorage(r.GetString(4)),
            r.GetInt32(5),
            ReadDto(r, 6),
            ReadDtoN(r, 7),
            ReadDtoN(r, 8),
            r.IsDBNull(9) ? null : r.GetString(9));
    }

    private static DateTimeOffset ReadDto(DbDataReader r, int i)
        => DateTimeOffset.Parse(r.GetString(i), CultureInfo.InvariantCulture);

    private static DateTimeOffset? ReadDtoN(DbDataReader r, int i)
        => r.IsDBNull(i) ? null : DateTimeOffset.Parse(r.GetString(i), CultureInfo.InvariantCulture);
}
