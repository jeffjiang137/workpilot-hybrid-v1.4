using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using WorkPilot.Application.Security.Retention;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;

namespace WorkPilot.Infrastructure.Security;

/// <summary>
/// SQLite implementation of <see cref="IRetentionStore"/> (doc 05 §9, SEC-106). Every delete is bounded
/// by <paramref name="batchSize"/> and runs inside a transaction so a single batch stays within the
/// ~200 ms budget. Terminal runs are limited to <c>completed</c>/<c>failed</c>/<c>cancelled</c> and only
/// those with a non-null <c>finished_at_utc</c> before the cutoff; protected states (waiting_approval,
/// needs_review) and open incidents are never returned, satisfying "protected records are not deleted
/// by time". Holds no secrets.
/// </summary>
public sealed class SqliteRetentionStore : IRetentionStore
{
    // Terminal run statuses eligible for time-based cleanup (doc 05 §9). Non-terminal / protected
    // states (waiting_approval, needs_review, running, queued, blocked_policy) are excluded on purpose.
    private const string TerminalRunStatuses = "'completed','failed','cancelled'";
    private const int ResolvedIncidentState = 3; // IncidentState.Resolved

    private readonly SqliteConnection _connection;

    public SqliteRetentionStore(SqliteConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    public async Task<Result<IReadOnlyList<RunId>>> GetDeletableRunIdsAsync(
        DateTimeOffset runCutoff, int batchSize, CancellationToken ct = default)
    {
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT id FROM automation_runs
                WHERE status IN ({TerminalRunStatuses})
                  AND finished_at_utc IS NOT NULL
                  AND finished_at_utc < $cutoff
                ORDER BY finished_at_utc ASC
                LIMIT $batch
                """;
            cmd.Parameters.AddWithValue("$cutoff", runCutoff.ToString("O"));
            cmd.Parameters.AddWithValue("$batch", batchSize);
            var ids = new List<RunId>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                ids.Add(RunId.Parse(reader.GetString(0)));
            return Result<IReadOnlyList<RunId>>.Ok(ids);
        }
        catch (Exception error)
        {
            return Result<IReadOnlyList<RunId>>.Fail(RetentionAndExportErrors.RetentionStoreError($"get_runs: {error.Message}"));
        }
    }

    public async Task<Result<int>> DeleteRunCascadeAsync(RunId id, CancellationToken ct = default)
    {
        try
        {
            await using var tx = await _connection.BeginTransactionAsync(ct);
            try
            {
                var steps = _connection.CreateCommand();
                steps.Transaction = (SqliteTransaction)tx;
                steps.CommandText = "DELETE FROM automation_step_runs WHERE run_id=$id";
                steps.Parameters.AddWithValue("$id", id.Value);
                var stepsDeleted = await steps.ExecuteNonQueryAsync(ct);

                var events = _connection.CreateCommand();
                events.Transaction = (SqliteTransaction)tx;
                events.CommandText = "DELETE FROM run_events WHERE run_id=$id";
                events.Parameters.AddWithValue("$id", id.Value);
                var eventsDeleted = await events.ExecuteNonQueryAsync(ct);

                var run = _connection.CreateCommand();
                run.Transaction = (SqliteTransaction)tx;
                run.CommandText = "DELETE FROM automation_runs WHERE id=$id";
                run.Parameters.AddWithValue("$id", id.Value);
                var runDeleted = await run.ExecuteNonQueryAsync(ct);

                await tx.CommitAsync(ct);
                return Result<int>.Ok(stepsDeleted + eventsDeleted + runDeleted);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }
        catch (Exception error)
        {
            return Result<int>.Fail(RetentionAndExportErrors.RetentionStoreError($"delete_run: {error.Message}"));
        }
    }

    public async Task<Result<int>> DeleteRunEventsOlderThanAsync(
        DateTimeOffset eventCutoff, int batchSize, CancellationToken ct = default)
    {
        try
        {
            // Only events of runs that are retained (not waiting_approval / needs_review) are pruned
            // here; events of runs queued for cascade-deletion are handled by DeleteRunCascadeAsync.
            // The bundled SQLite rejects LIMIT directly on DELETE, so we bound the batch via a
            // rowid-in-subquery (SELECT ... LIMIT), preserving the ~200 ms / batchSize budget.
            var cmd = _connection.CreateCommand();
            cmd.CommandText = $"""
                DELETE FROM run_events
                WHERE rowid IN (
                  SELECT rowid FROM run_events
                  WHERE occurred_at_utc < $cutoff
                    AND run_id IN (SELECT id FROM automation_runs
                                   WHERE status NOT IN ('waiting_approval','needs_review'))
                  LIMIT $batch)
                """;
            cmd.Parameters.AddWithValue("$cutoff", eventCutoff.ToString("O"));
            cmd.Parameters.AddWithValue("$batch", batchSize);
            var deleted = await cmd.ExecuteNonQueryAsync(ct);
            return Result<int>.Ok(deleted);
        }
        catch (Exception error)
        {
            return Result<int>.Fail(RetentionAndExportErrors.RetentionStoreError($"delete_events: {error.Message}"));
        }
    }

    public async Task<Result<int>> DeleteAuditRecordsOlderThanAsync(
        DateTimeOffset auditCutoff, int batchSize, CancellationToken ct = default)
    {
        try
        {
            // NOTE (SEC-106 / doc 05 §6.1): the audit log is HMAC-chained per UTC day. Deleting a slice
            // breaks the chain for the lowest retained record, which the integrity verifier surfaces as a
            // Critical Incident on next check — by design the audit log *detects* tampering/deletion
            // rather than preventing it. Retention of the audit log is therefore a deliberate, audited
            // action (the cleanup itself writes a retention_cleanup audit entry upstream).
            // Bounded via rowid-in-subquery because the bundled SQLite rejects LIMIT on DELETE.
            var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                DELETE FROM security_audit_log
                WHERE rowid IN (
                  SELECT rowid FROM security_audit_log
                  WHERE occurred_at_utc < $cutoff
                  LIMIT $batch)
                """;
            cmd.Parameters.AddWithValue("$cutoff", auditCutoff.ToString("O"));
            cmd.Parameters.AddWithValue("$batch", batchSize);
            var deleted = await cmd.ExecuteNonQueryAsync(ct);
            return Result<int>.Ok(deleted);
        }
        catch (Exception error)
        {
            return Result<int>.Fail(RetentionAndExportErrors.RetentionStoreError($"delete_audit: {error.Message}"));
        }
    }

    public async Task<Result<int>> DeleteResolvedIncidentsOlderThanAsync(
        DateTimeOffset auditCutoff, int batchSize, CancellationToken ct = default)
    {
        try
        {
            // Only resolved incidents are pruned; open / acknowledged / mitigated / reopened incidents
            // are protected (doc 05 §9). Bounded via rowid-in-subquery (LIMIT on DELETE unsupported).
            var cmd = _connection.CreateCommand();
            cmd.CommandText = $"""
                DELETE FROM incidents
                WHERE rowid IN (
                  SELECT rowid FROM incidents
                  WHERE state = {ResolvedIncidentState}
                    AND last_seen_utc < $cutoff
                  LIMIT $batch)
                """;
            cmd.Parameters.AddWithValue("$cutoff", auditCutoff.ToString("O"));
            cmd.Parameters.AddWithValue("$batch", batchSize);
            var deleted = await cmd.ExecuteNonQueryAsync(ct);
            return Result<int>.Ok(deleted);
        }
        catch (Exception error)
        {
            return Result<int>.Fail(RetentionAndExportErrors.RetentionStoreError($"delete_incidents: {error.Message}"));
        }
    }
}
