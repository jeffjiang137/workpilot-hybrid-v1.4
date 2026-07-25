using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using WorkPilot.Application.Security;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Security;
using WorkPilot.Domain.Security.Audit;

namespace WorkPilot.Infrastructure.Security;

/// <summary>
/// SQLite implementation of the security persistence ports (T19 / SEC-102/103/106). Implements
/// <see cref="ISecurityEventStore"/>, <see cref="IIncidentStore"/>, <see cref="IAuditLogStore"/> and
/// <see cref="IDetectorActionStore"/> against the four Migration 020 tables. The store is purely a
/// faithful reader/writer: HMAC chaining and incident aggregation happen in the Application layer.
/// All rows are display-name-free (doc 06 §2) and the audit log preserves the HMAC computed upstream.
/// </summary>
public sealed class SecuritySqliteStore : ISecurityEventStore, IIncidentStore, IAuditLogStore, IDetectorActionStore
{
    private readonly SqliteConnection _connection;

    public SecuritySqliteStore(SqliteConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    // ---- ISecurityEventStore (SEC-102) ----

    public async Task<Result> AppendAsync(SecurityEvent e, CancellationToken ct)
    {
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO security_events(id,occurred_at_utc,type,severity,fingerprint,source_kind,source_id,automation_id,run_id,safe_evidence_json,detector_version,created_at_utc)
                VALUES($id,$occ,$type,$sev,$fp,$sk,$sid,$auto,$run,$ev,$ver,$now)
                """;
            cmd.Parameters.AddWithValue("$id", e.Id.Value);
            cmd.Parameters.AddWithValue("$occ", e.OccurredAtUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$type", (int)e.Type);
            cmd.Parameters.AddWithValue("$sev", (int)e.Severity);
            cmd.Parameters.AddWithValue("$fp", e.Fingerprint);
            cmd.Parameters.AddWithValue("$sk", (object?)e.Source?.Kind ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$sid", (object?)e.Source?.Id ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$auto", e.AutomationId is { } a ? (object)a.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("$run", e.RunId is { } r ? (object)r.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("$ev", JsonSerializer.Serialize(e.SafeEvidence));
            cmd.Parameters.AddWithValue("$ver", e.DetectorVersion);
            cmd.Parameters.AddWithValue("$now", e.OccurredAtUtc.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
            return Result.Success();
        }
        catch (Exception error)
        {
            return Result.Failure(SecurityErrors.AuditWriteFailedError($"security_events: {error.Message}"));
        }
    }

    public async Task<IReadOnlyList<SecurityEvent>> ListRecentAsync(int limit, CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id,occurred_at_utc,type,severity,fingerprint,source_kind,source_id,automation_id,run_id,safe_evidence_json,detector_version FROM security_events ORDER BY occurred_at_utc DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);
        var list = new List<SecurityEvent>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(ReadEvent(reader));
        return list;
    }

    public async Task<bool> ExistsRecentAsync(string fingerprint, DateTimeOffset since, CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM security_events WHERE fingerprint=$fp AND occurred_at_utc >= $since";
        cmd.Parameters.AddWithValue("$fp", fingerprint);
        cmd.Parameters.AddWithValue("$since", since.ToString("O"));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) > 0;
    }

    // ---- IIncidentStore (SEC-103) ----

    public async Task<Incident?> GetOpenByFingerprintAsync(string fingerprint, DateTimeOffset since, CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        // Returns the most recent incident for the fingerprint within the sliding window, regardless of
        // state — the aggregator decides open/merge/reopen (doc 06 §3; re-open after Resolved).
        cmd.CommandText = "SELECT id,fingerprint,state,severity,type,first_seen_utc,last_seen_utc,count,recent_evidence_digests_json,resolution_code,resolution_note,resolved_at_utc,created_at_utc,updated_at_utc,last_action_id FROM incidents WHERE fingerprint=$fp AND last_seen_utc >= $since ORDER BY last_seen_utc DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$fp", fingerprint);
        cmd.Parameters.AddWithValue("$since", since.ToString("O"));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadIncident(reader) : null;
    }

    public async Task<Incident?> GetByIdAsync(IncidentId id, CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id,fingerprint,state,severity,type,first_seen_utc,last_seen_utc,count,recent_evidence_digests_json,resolution_code,resolution_note,resolved_at_utc,created_at_utc,updated_at_utc,last_action_id FROM incidents WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id.Value);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadIncident(reader) : null;
    }

    public async Task InsertAsync(Incident incident, CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO incidents(id,fingerprint,state,severity,type,first_seen_utc,last_seen_utc,count,recent_evidence_digests_json,resolution_code,resolution_note,resolved_at_utc,created_at_utc,updated_at_utc,last_action_id)
            VALUES($id,$fp,$state,$sev,$type,$first,$last,$count,$digests,$rc,$rn,$ra,$created,$updated,$action)
            """;
        BindIncident(cmd, incident);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateAsync(Incident incident, CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE incidents SET state=$state,severity=$sev,type=$type,last_seen_utc=$last,count=$count,recent_evidence_digests_json=$digests,resolution_code=$rc,resolution_note=$rn,resolved_at_utc=$ra,updated_at_utc=$updated,last_action_id=$action
            WHERE id=$id
            """;
        BindIncident(cmd, incident, bindIdOnly: false);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<Incident>> ListAsync(IncidentState? state, int limit, CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        if (state is null)
            cmd.CommandText = "SELECT id,fingerprint,state,severity,type,first_seen_utc,last_seen_utc,count,recent_evidence_digests_json,resolution_code,resolution_note,resolved_at_utc,created_at_utc,updated_at_utc,last_action_id FROM incidents ORDER BY last_seen_utc DESC LIMIT $limit";
        else
        {
            cmd.CommandText = "SELECT id,fingerprint,state,severity,type,first_seen_utc,last_seen_utc,count,recent_evidence_digests_json,resolution_code,resolution_note,resolved_at_utc,created_at_utc,updated_at_utc,last_action_id FROM incidents WHERE state=$state ORDER BY last_seen_utc DESC LIMIT $limit";
            cmd.Parameters.AddWithValue("$state", (int)state.Value);
        }

        cmd.Parameters.AddWithValue("$limit", limit);
        var list = new List<Incident>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(ReadIncident(reader));
        return list;
    }

    // ---- IAuditLogStore (SEC-106) ----

    public async Task<Result<AuditEntry>> AppendAsync(AuditEntry entry, CancellationToken ct)
    {
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO security_audit_log(sequence,occurred_at_utc,category,action,actor,subject_json,decision_trace_json,safe_detail_json,prev_hmac,hmac,created_at_utc)
                VALUES($seq,$occ,$cat,$action,$actor,$subject,$trace,$detail,$prev,$hmac,$created)
                """;
            cmd.Parameters.AddWithValue("$seq", entry.Sequence);
            cmd.Parameters.AddWithValue("$occ", entry.OccurredAtUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$cat", (int)entry.Category);
            cmd.Parameters.AddWithValue("$action", entry.Action);
            cmd.Parameters.AddWithValue("$actor", entry.Actor);
            cmd.Parameters.AddWithValue("$subject", entry.SubjectJson);
            cmd.Parameters.AddWithValue("$trace", entry.DecisionTraceJson);
            cmd.Parameters.AddWithValue("$detail", entry.SafeDetailJson);
            cmd.Parameters.AddWithValue("$prev", entry.PrevHmac);
            cmd.Parameters.AddWithValue("$hmac", entry.Hmac);
            cmd.Parameters.AddWithValue("$created", entry.CreatedAtUtc.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
            return Result<AuditEntry>.Ok(entry);
        }
        catch (Exception error)
        {
            return Result<AuditEntry>.Fail(SecurityErrors.AuditWriteFailedError($"security_audit_log: {error.Message}"));
        }
    }

    public async Task<AuditEntry?> GetLastAsync(CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT sequence,occurred_at_utc,category,action,actor,subject_json,decision_trace_json,safe_detail_json,prev_hmac,hmac,created_at_utc FROM security_audit_log ORDER BY sequence DESC LIMIT 1";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadAudit(reader) : null;
    }

    public async Task<IReadOnlyList<AuditEntry>> GetAllAsync(CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT sequence,occurred_at_utc,category,action,actor,subject_json,decision_trace_json,safe_detail_json,prev_hmac,hmac,created_at_utc FROM security_audit_log ORDER BY sequence ASC";
        var list = new List<AuditEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(ReadAudit(reader));
        return list;
    }

    // ---- IDetectorActionStore (doc 06 §4 idempotency) ----

    public async Task<bool> TryMarkAppliedAsync(string actionId, CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "INSERT INTO detector_actions(action_id,applied_at_utc) VALUES($id,$now) ON CONFLICT(action_id) DO NOTHING";
        cmd.Parameters.AddWithValue("$id", actionId);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        var rows = await cmd.ExecuteNonQueryAsync(ct);
        return rows == 1; // true only the first time this action id is seen
    }

    // ---- readers / binders ----

    private static SecurityEvent ReadEvent(DbDataReader r)
    {
        var evidence = JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(9)) ?? new Dictionary<string, string>();
        SourceReference? source = r.IsDBNull(5) || r.IsDBNull(6)
            ? null
            : new SourceReference(r.GetString(5), r.GetString(6));
        AutomationId? auto = r.IsDBNull(7) ? (AutomationId?)null : AutomationId.Parse(r.GetString(7));
        RunId? run = r.IsDBNull(8) ? (RunId?)null : RunId.Parse(r.GetString(8));
        return new SecurityEvent(
            SecurityEventId.Parse(r.GetString(0)),
            DateTimeOffset.Parse(r.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
            (SecurityEventType)r.GetInt32(2),
            (SecuritySeverity)r.GetInt32(3),
            r.GetString(4),
            source,
            auto,
            run,
            evidence,
            r.GetString(10));
    }

    private static Incident ReadIncident(DbDataReader r)
    {
        var digests = JsonSerializer.Deserialize<List<string>>(r.GetString(8)) ?? new List<string>();
        return new Incident(
            Id: IncidentId.Parse(r.GetString(0)),
            Fingerprint: r.GetString(1),
            State: (IncidentState)r.GetInt32(2),
            Severity: (SecuritySeverity)r.GetInt32(3),
            Type: (SecurityEventType)r.GetInt32(4),
            FirstSeenUtc: DateTimeOffset.Parse(r.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
            LastSeenUtc: DateTimeOffset.Parse(r.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
            Count: r.GetInt32(7),
            RecentEvidenceDigests: digests,
            ResolutionCode: r.IsDBNull(9) ? null : r.GetString(9),
            ResolutionNote: r.IsDBNull(10) ? null : r.GetString(10),
            ResolvedAtUtc: r.IsDBNull(11) ? (DateTimeOffset?)null : DateTimeOffset.Parse(r.GetString(11), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
            CreatedAtUtc: DateTimeOffset.Parse(r.GetString(12), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
            UpdatedAtUtc: DateTimeOffset.Parse(r.GetString(13), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
            LastActionId: r.IsDBNull(14) ? null : r.GetString(14));
    }

    private static void BindIncident(SqliteCommand cmd, Incident incident, bool bindIdOnly = false)
    {
        cmd.Parameters.AddWithValue("$id", incident.Id.Value);
        cmd.Parameters.AddWithValue("$fp", incident.Fingerprint);
        cmd.Parameters.AddWithValue("$state", (int)incident.State);
        cmd.Parameters.AddWithValue("$sev", (int)incident.Severity);
        cmd.Parameters.AddWithValue("$type", (int)incident.Type);
        cmd.Parameters.AddWithValue("$first", incident.FirstSeenUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$last", incident.LastSeenUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$count", incident.Count);
        cmd.Parameters.AddWithValue("$digests", JsonSerializer.Serialize(incident.RecentEvidenceDigests));
        cmd.Parameters.AddWithValue("$rc", (object?)incident.ResolutionCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rn", (object?)incident.ResolutionNote ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ra", incident.ResolvedAtUtc is { } ra ? (object)ra.ToString("O") : DBNull.Value);
        cmd.Parameters.AddWithValue("$created", incident.CreatedAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$updated", incident.UpdatedAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$action", (object?)incident.LastActionId ?? DBNull.Value);
    }

    private static AuditEntry ReadAudit(DbDataReader r)
        => new(
            Sequence: r.GetInt64(0),
            OccurredAtUtc: DateTimeOffset.Parse(r.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
            Category: (AuditCategory)r.GetInt32(2),
            Action: r.GetString(3),
            Actor: r.GetString(4),
            SubjectJson: r.GetString(5),
            DecisionTraceJson: r.GetString(6),
            SafeDetailJson: r.GetString(7),
            PrevHmac: r.GetString(8),
            Hmac: r.GetString(9),
            CreatedAtUtc: DateTimeOffset.Parse(r.GetString(10), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal));
}
