using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using WorkPilot.Application.Automation.Materialization;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation;

namespace WorkPilot.Infrastructure.Automation.Materialization;

/// <summary>
/// SQLite implementation of <see cref="ITriggerScheduleStore"/> over <c>automation_schedules</c>.
/// One row per (revision, trigger); the <c>next_occurrence_at_utc</c> column feeds the due index and
/// the materializer advances <c>last_materialized_at_utc</c> after each pass so a crash resumes
/// exactly where it stopped. Domain-event / manual triggers carry a NULL next instant (never due).
/// </summary>
public sealed class TriggerScheduleRepository : ITriggerScheduleStore
{
    private readonly SqliteConnection _connection;

    public TriggerScheduleRepository(SqliteConnection connection) => _connection = connection;

    public async Task<Result> UpsertAsync(
        AutomationId automationId, AutomationRevisionId revisionId, TriggerDefinition trigger,
        DateTimeOffset? nextOccurrenceAtUtc, DateTimeOffset now, CancellationToken ct)
    {
        var isTimeBased = trigger.Type is TriggerType.Interval or TriggerType.Once
            or TriggerType.CalendarDaily or TriggerType.CalendarWeekly or TriggerType.CalendarMonthly;
        var next = isTimeBased ? nextOccurrenceAtUtc : null;

        var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO automation_schedules(automation_revision_id,trigger_id,trigger_kind,timezone_id,schedule_json,next_occurrence_at_utc,last_materialized_at_utc,enabled,row_version)
            VALUES($rev,$tid,$tk,$tz,$json,$next,$last,1,1)
            ON CONFLICT(automation_revision_id,trigger_id) DO UPDATE SET
              trigger_kind=excluded.trigger_kind, timezone_id=excluded.timezone_id, schedule_json=excluded.schedule_json,
              next_occurrence_at_utc=excluded.next_occurrence_at_utc, enabled=1, row_version=row_version+1
            """;
        cmd.Parameters.AddWithValue("$rev", revisionId.Value);
        cmd.Parameters.AddWithValue("$tid", trigger.TriggerId);
        cmd.Parameters.AddWithValue("$tk", trigger.Type.ToStorage());
        cmd.Parameters.AddWithValue("$tz", (object?)trigger.TimezoneId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$json", trigger.ToCanonicalJson().ToJsonString());
        cmd.Parameters.AddWithValue("$next", (object?)next?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$last", DBNull.Value);
        try
        {
            await cmd.ExecuteNonQueryAsync(ct);
            return Result.Success();
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            return Result.Failure(MapStoreError(ex));
        }
    }

    public async Task<Result<IReadOnlyList<DueSchedule>>> GetDueSchedulesAsync(DateTimeOffset now, int batchSize, CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT d.id, s.automation_revision_id, s.trigger_id, s.last_materialized_at_utc, s.next_occurrence_at_utc
            FROM automation_schedules s
            JOIN automation_revisions r ON r.id = s.automation_revision_id
            JOIN automation_definitions d ON d.id = r.automation_id
            WHERE s.enabled=1 AND s.next_occurrence_at_utc IS NOT NULL AND s.next_occurrence_at_utc <= $now
            ORDER BY s.next_occurrence_at_utc ASC
            LIMIT $batch
            """;
        cmd.Parameters.AddWithValue("$now", now.ToString("O"));
        cmd.Parameters.AddWithValue("$batch", batchSize);
        var list = new List<DueSchedule>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(MapDue(reader));
        return Result<IReadOnlyList<DueSchedule>>.Ok(list);
    }

    public async Task<Result> UpdatePointerAsync(
        AutomationId automationId, AutomationRevisionId revisionId, string triggerId,
        DateTimeOffset lastMaterializedAtUtc, DateTimeOffset? nextOccurrenceAtUtc, CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE automation_schedules
            SET last_materialized_at_utc=$last, next_occurrence_at_utc=$next, row_version=row_version+1
            WHERE automation_revision_id=$rev AND trigger_id=$tid
            """;
        cmd.Parameters.AddWithValue("$last", lastMaterializedAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$next", (object?)nextOccurrenceAtUtc?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rev", revisionId.Value);
        cmd.Parameters.AddWithValue("$tid", triggerId);
        try
        {
            await cmd.ExecuteNonQueryAsync(ct);
            return Result.Success();
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            return Result.Failure(MapStoreError(ex));
        }
    }

    public async Task<Result<IReadOnlyList<DueSchedule>>> GetDomainEventSchedulesAsync(string spaceId, string eventType, CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT d.id, s.automation_revision_id, s.trigger_id, s.last_materialized_at_utc, s.next_occurrence_at_utc, s.schedule_json
            FROM automation_schedules s
            JOIN automation_revisions r ON r.id = s.automation_revision_id
            JOIN automation_definitions d ON d.id = r.automation_id
            WHERE d.space_id=$space AND s.enabled=1
            LIMIT 500
            """;
        cmd.Parameters.AddWithValue("$space", spaceId);
        var list = new List<DueSchedule>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            TriggerDefinition? trigger = null;
            try { trigger = TriggerDefinition.FromJson(System.Text.Json.Nodes.JsonNode.Parse(reader.GetString(5))!); }
            catch (System.Text.Json.JsonException) { trigger = null; }

            if (trigger is null || trigger.Type != TriggerType.DomainEvent) continue;
            if (!string.Equals(trigger.EventType, eventType, StringComparison.Ordinal)) continue;
            list.Add(MapDue(reader, skipJson: true));
        }
        return Result<IReadOnlyList<DueSchedule>>.Ok(list);
    }

    private static DueSchedule MapDue(DbDataReader r, bool skipJson = false)
    {
        var last = r.IsDBNull(3) ? (DateTimeOffset?)null : DateTimeOffset.Parse(r.GetString(3), CultureInfo.InvariantCulture);
        var next = r.IsDBNull(4) ? (DateTimeOffset?)null : DateTimeOffset.Parse(r.GetString(4), CultureInfo.InvariantCulture);
        return new DueSchedule(
            AutomationId.Parse(r.GetString(0)),
            AutomationRevisionId.Parse(r.GetString(1)),
            r.GetString(2),
            last,
            next);
    }

    private static AppError MapStoreError(Exception ex)
        => new AppError("SCHEDULE_STORE", ErrorCategory.Database, "Schedule.StoreError", false,
            new Dictionary<string, string> { ["detail"] = ex.Message });
}
