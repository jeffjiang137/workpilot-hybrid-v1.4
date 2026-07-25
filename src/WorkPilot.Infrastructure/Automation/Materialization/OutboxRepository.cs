using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using WorkPilot.Application.Automation.Materialization;
using WorkPilot.Contracts.Primitives;

namespace WorkPilot.Infrastructure.Automation.Materialization;

/// <summary>
/// SQLite implementation of <see cref="IDomainEventOutboxStore"/> over <c>domain_event_outbox</c>
/// (spec doc 04 §4). Reads pending events oldest-first; records dispatch or a bounded-retry failure.
/// After <paramref name="maxAttempts"/> attempts a row is left undispatched for incident generation
/// (T19) — this store only records the last error and backs off.
/// </summary>
public sealed class OutboxRepository : IDomainEventOutboxStore
{
    private readonly SqliteConnection _connection;

    public OutboxRepository(SqliteConnection connection) => _connection = connection;

    public async Task<Result<IReadOnlyList<PendingOutboxEvent>>> GetPendingAsync(int batchSize, CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, event_type, space_id, entity_type, entity_id, entity_version, safe_payload_json, occurred_at_utc, attempt_count
            FROM domain_event_outbox
            WHERE dispatched_at_utc IS NULL AND (next_attempt_at_utc IS NULL OR next_attempt_at_utc <= $now)
            ORDER BY occurred_at_utc ASC
            LIMIT $batch
            """;
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$batch", batchSize);
        var list = new List<PendingOutboxEvent>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new PendingOutboxEvent(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5),
                reader.GetString(6),
                DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
                reader.GetInt32(8)));
        }
        return Result<IReadOnlyList<PendingOutboxEvent>>.Ok(list);
    }

    public async Task<Result> MarkDispatchedAsync(string outboxId, DateTimeOffset now, CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE domain_event_outbox SET dispatched_at_utc=$now, next_attempt_at_utc=NULL WHERE id=$id
            """;
        cmd.Parameters.AddWithValue("$now", now.ToString("O"));
        cmd.Parameters.AddWithValue("$id", outboxId);
        try { await cmd.ExecuteNonQueryAsync(ct); return Result.Success(); }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            return Result.Failure(MapStoreError(ex));
        }
    }

    public async Task<Result> MarkFailedAsync(string outboxId, DateTimeOffset now, DateTimeOffset nextAttemptAt, string? errorCode, int maxAttempts, CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE domain_event_outbox
            SET attempt_count = attempt_count + 1,
                next_attempt_at_utc = CASE WHEN attempt_count + 1 >= $max THEN NULL ELSE $next END,
                last_error_code = $err
            WHERE id=$id
            """;
        cmd.Parameters.AddWithValue("$max", maxAttempts);
        cmd.Parameters.AddWithValue("$next", nextAttemptAt.ToString("O"));
        cmd.Parameters.AddWithValue("$err", (object?)errorCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", outboxId);
        try { await cmd.ExecuteNonQueryAsync(ct); return Result.Success(); }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            return Result.Failure(MapStoreError(ex));
        }
    }

    private static AppError MapStoreError(Exception ex)
        => new AppError("OUTBOX_STORE", ErrorCategory.Database, "Outbox.StoreError", false,
            new Dictionary<string, string> { ["detail"] = ex.Message });
}
