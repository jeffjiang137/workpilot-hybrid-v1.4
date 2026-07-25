using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using WorkPilot.Application.Security.Retention;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Domain.Security.Retention;

namespace WorkPilot.Infrastructure.Security;

/// <summary>
/// SQLite implementation of <see cref="IRetentionSettingsStore"/> (doc 05 §9, SEC-106). A single-row
/// settings document in the Migration 022 <c>retention_settings</c> table (singleton_id = 1). When the
/// row is absent (fresh database), <see cref="GetAsync"/> returns <see cref="RetentionSettings.Default"/>
/// so cleanup never stalls; the first explicit save seeds the row. Holds no secrets.
/// </summary>
public sealed class SqliteRetentionSettingsStore : IRetentionSettingsStore
{
    private readonly SqliteConnection _connection;
    private readonly IClock _clock;

    public SqliteRetentionSettingsStore(SqliteConnection connection, IClock clock)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<Result<RetentionSettings>> GetAsync(CancellationToken ct = default)
    {
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT run_days, event_days, audit_days, last_cleanup_at_utc, updated_at_utc
                FROM retention_settings WHERE singleton_id = 1
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return Result<RetentionSettings>.Ok(RetentionSettings.Default);

            var policy = new RetentionPolicy(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetInt32(2));
            var lastCleanup = reader.IsDBNull(3)
                ? (DateTimeOffset?)null
                : DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
            return Result<RetentionSettings>.Ok(new RetentionSettings(policy, lastCleanup));
        }
        catch (Exception error)
        {
            return Result<RetentionSettings>.Fail(RetentionAndExportErrors.RetentionStoreError($"get: {error.Message}"));
        }
    }

    public async Task<Result> SaveAsync(RetentionSettings settings, CancellationToken ct = default)
    {
        try
        {
            var now = _clock.UtcNow.ToString("O");
            var last = settings.LastCleanupAtUtc?.ToString("O");
            var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO retention_settings(singleton_id, run_days, event_days, audit_days, last_cleanup_at_utc, updated_at_utc, row_version)
                VALUES(1, $run, $event, $audit, $last, $now, 1)
                ON CONFLICT(singleton_id) DO UPDATE SET
                  run_days = excluded.run_days,
                  event_days = excluded.event_days,
                  audit_days = excluded.audit_days,
                  last_cleanup_at_utc = excluded.last_cleanup_at_utc,
                  updated_at_utc = excluded.updated_at_utc,
                  row_version = retention_settings.row_version + 1
                """;
            cmd.Parameters.AddWithValue("$run", settings.Policy.RunDays);
            cmd.Parameters.AddWithValue("$event", settings.Policy.EventDays);
            cmd.Parameters.AddWithValue("$audit", settings.Policy.AuditDays);
            cmd.Parameters.AddWithValue("$last", (object?)last ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", now);
            await cmd.ExecuteNonQueryAsync(ct);
            return Result.Success();
        }
        catch (Exception error)
        {
            return Result.Failure(RetentionAndExportErrors.RetentionStoreError($"save: {error.Message}"));
        }
    }
}
