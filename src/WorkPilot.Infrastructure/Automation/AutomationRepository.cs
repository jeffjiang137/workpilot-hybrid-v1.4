using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using WorkPilot.Application.Automation;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation;

namespace WorkPilot.Infrastructure.Automation;

/// <summary>
/// SQLite implementation of <see cref="IAutomationRepository"/> against the T03 (017) schema.
/// Handles the definition <-> revision circular FK by inserting the definition with a NULL
/// current_revision_id first, then the revision, then pointing the definition at it.
/// Optimistic concurrency is enforced via <c>row_version</c> (AUT-008).
/// </summary>
public sealed class AutomationRepository : IAutomationRepository
{
    private readonly SqliteConnection _connection;

    public AutomationRepository(SqliteConnection connection) => _connection = connection;

    public async Task<Result<AutomationDefinition>> GetAsync(AutomationId id, CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id,space_id,name,description,lifecycle,current_revision_id,revision_number,row_version,created_at_utc,updated_at_utc FROM automation_definitions WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id.Value);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return AutomationErrors.NotFoundError();
        return Result<AutomationDefinition>.Ok(MapDefinition(reader));
    }

    public async Task<Result<IReadOnlyList<AutomationDefinition>>> ListBySpaceAsync(SpaceId spaceId, bool includeDeleted, CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = includeDeleted
            ? "SELECT id,space_id,name,description,lifecycle,current_revision_id,revision_number,row_version,created_at_utc,updated_at_utc FROM automation_definitions WHERE space_id=$sid ORDER BY name"
            : "SELECT id,space_id,name,description,lifecycle,current_revision_id,revision_number,row_version,created_at_utc,updated_at_utc FROM automation_definitions WHERE space_id=$sid AND lifecycle<>'deleted' ORDER BY name";
        cmd.Parameters.AddWithValue("$sid", spaceId.Value);
        var items = new List<AutomationDefinition>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) items.Add(MapDefinition(reader));
        return Result<IReadOnlyList<AutomationDefinition>>.Ok(items);
    }

    public async Task<Result<IReadOnlyList<AutomationRevision>>> GetRevisionsAsync(AutomationId id, CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id,automation_id,revision_number,trigger_json,workflow_json,binding_json,budget_json,overlap_policy,missed_run_policy,permission_request_json,canonical_sha256,created_at_utc FROM automation_revisions WHERE automation_id=$id ORDER BY revision_number";
        cmd.Parameters.AddWithValue("$id", id.Value);
        var items = new List<AutomationRevision>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) items.Add(MapRevision(reader));
        return Result<IReadOnlyList<AutomationRevision>>.Ok(items);
    }

    public async Task<Result<AutomationRevision>> GetRevisionAsync(AutomationRevisionId revisionId, CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id,automation_id,revision_number,trigger_json,workflow_json,binding_json,budget_json,overlap_policy,missed_run_policy,permission_request_json,canonical_sha256,created_at_utc FROM automation_revisions WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", revisionId.Value);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return AutomationErrors.RevisionNotFoundError();
        return Result<AutomationRevision>.Ok(MapRevision(reader));
    }

    public async Task<Result<IReadOnlyList<AutomationDefinition>>> ListEnabledAsync(CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id,space_id,name,description,lifecycle,current_revision_id,revision_number,row_version,created_at_utc,updated_at_utc FROM automation_definitions WHERE lifecycle='enabled' ORDER BY name";
        var items = new List<AutomationDefinition>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) items.Add(MapDefinition(reader));
        return Result<IReadOnlyList<AutomationDefinition>>.Ok(items);
    }

    public async Task<Result<AutomationDefinition>> SaveAsync(AutomationDefinition definition, AutomationRevision? newRevision, CancellationToken ct)
    {
        await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(ct);
        try
        {
            var existing = await GetRowVersionAsync(definition.Id, tx, ct);
            var isInsert = !existing.HasValue;
            if (!isInsert && existing.Value != definition.RowVersion)
                return AutomationErrors.ConcurrencyConflictError(); // AUT-008

            var newRowVersion = isInsert ? 1 : existing.Value + 1;

            // Inserts: write the definition with a NULL current_revision_id first to satisfy the
            // circular FK, then insert the revision, then point the definition at it.
            var insertNullRevision = isInsert && newRevision is not null;

            var defCmd = _connection.CreateCommand();
            defCmd.Transaction = (SqliteTransaction)tx;
            if (isInsert)
            {
                defCmd.CommandText = @"INSERT INTO automation_definitions(id,space_id,name,description,lifecycle,current_revision_id,revision_number,row_version,created_at_utc,updated_at_utc)
                    VALUES($id,$space,$name,$desc,$life,$cur,$rev,$rv,$created,$updated)";
                AddDefParams(defCmd, definition, newRowVersion, insertNullRevision ? null : definition.CurrentRevisionId.Value);
            }
            else
            {
                defCmd.CommandText = @"UPDATE automation_definitions SET name=$name,description=$desc,lifecycle=$life,current_revision_id=$cur,revision_number=$rev,row_version=$rv,updated_at_utc=$updated WHERE id=$id";
                defCmd.Parameters.AddWithValue("$name", definition.Name);
                defCmd.Parameters.AddWithValue("$desc", definition.Description);
                defCmd.Parameters.AddWithValue("$life", definition.Lifecycle.ToStorage());
                defCmd.Parameters.AddWithValue("$cur", newRevision is not null ? DBNull.Value : (object?)definition.CurrentRevisionId.Value);
                defCmd.Parameters.AddWithValue("$rev", definition.RevisionNumber);
                defCmd.Parameters.AddWithValue("$rv", newRowVersion);
                defCmd.Parameters.AddWithValue("$updated", definition.UpdatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
                defCmd.Parameters.AddWithValue("$id", definition.Id.Value);
            }

            await defCmd.ExecuteNonQueryAsync(ct);

            if (newRevision is not null)
            {
                var revCmd = _connection.CreateCommand();
                revCmd.Transaction = (SqliteTransaction)tx;
                revCmd.CommandText = @"INSERT INTO automation_revisions(id,automation_id,revision_number,schema_version,trigger_json,workflow_json,binding_json,budget_json,overlap_policy,missed_run_policy,permission_request_json,canonical_sha256,created_at_utc)
                    VALUES($id,$aid,$num,$schema,$trig,$wf,$bind,$bud,$ov,$miss,$perm,$hash,$created)";
                revCmd.Parameters.AddWithValue("$id", newRevision.Id.Value);
                revCmd.Parameters.AddWithValue("$aid", newRevision.AutomationId.Value);
                revCmd.Parameters.AddWithValue("$num", newRevision.RevisionNumber);
                revCmd.Parameters.AddWithValue("$schema", newRevision.Workflow.SchemaVersion);
                revCmd.Parameters.AddWithValue("$trig", newRevision.Trigger.ToCanonicalJson().ToJsonString());
                revCmd.Parameters.AddWithValue("$wf", newRevision.Workflow.ToCanonicalJson().ToJsonString());
                revCmd.Parameters.AddWithValue("$bind", newRevision.Binding.ToCanonicalJson().ToJsonString());
                revCmd.Parameters.AddWithValue("$bud", newRevision.Budget.ToCanonicalJson().ToJsonString());
                revCmd.Parameters.AddWithValue("$ov", newRevision.OverlapPolicy.ToStorage());
                revCmd.Parameters.AddWithValue("$miss", newRevision.MissedRunPolicy.ToStorage());
                revCmd.Parameters.AddWithValue("$perm", newRevision.PermissionRequest.ToCanonicalJson().ToJsonString());
                revCmd.Parameters.AddWithValue("$hash", newRevision.CanonicalSha256);
                revCmd.Parameters.AddWithValue("$created", newRevision.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
                await revCmd.ExecuteNonQueryAsync(ct);

                if (newRevision is not null)
                {
                    var point = _connection.CreateCommand();
                    point.Transaction = (SqliteTransaction)tx;
                    point.CommandText = "UPDATE automation_definitions SET current_revision_id=$cur,revision_number=$rev WHERE id=$id";
                    point.Parameters.AddWithValue("$cur", newRevision.Id.Value);
                    point.Parameters.AddWithValue("$rev", newRevision.RevisionNumber);
                    point.Parameters.AddWithValue("$id", definition.Id.Value);
                    await point.ExecuteNonQueryAsync(ct);
                }
            }

            await tx.CommitAsync(ct);
            definition.RowVersion = newRowVersion;
            return Result<AutomationDefinition>.Ok(definition);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private static void AddDefParams(SqliteCommand cmd, AutomationDefinition def, long rowVersion, string? currentRevisionId)
    {
        cmd.Parameters.AddWithValue("$id", def.Id.Value);
        cmd.Parameters.AddWithValue("$space", def.SpaceId.Value);
        cmd.Parameters.AddWithValue("$name", def.Name);
        cmd.Parameters.AddWithValue("$desc", def.Description);
        cmd.Parameters.AddWithValue("$life", def.Lifecycle.ToStorage());
        cmd.Parameters.AddWithValue("$cur", (object?)currentRevisionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rev", def.RevisionNumber);
        cmd.Parameters.AddWithValue("$rv", rowVersion);
        cmd.Parameters.AddWithValue("$created", def.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$updated", def.UpdatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
    }

    private static AutomationDefinition MapDefinition(SqliteDataReader reader)
    {
        var curRevId = reader.IsDBNull(5)
            ? AutomationRevisionId.Parse(Guid.Empty.ToString("N"))
            : AutomationRevisionId.Parse(reader.GetString(5));
        return new AutomationDefinition(
            AutomationId.Parse(reader.GetString(0)),
            SpaceId.Parse(reader.GetString(1)),
            reader.GetString(2),
            reader.GetString(3),
            AutomationStorageMaps.LifecycleFromStorage(reader.GetString(4)),
            curRevId,
            reader.GetInt32(6),
            reader.GetInt64(7),
            DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture));
    }

    private static AutomationRevision MapRevision(SqliteDataReader reader)
    {
        var trigger = TriggerDefinition.FromJson(JsonNode.Parse(reader.GetString(3))!);
        var workflow = WorkflowDefinition.FromJson(JsonNode.Parse(reader.GetString(4))!);
        var binding = AutomationBinding.FromJson(JsonNode.Parse(reader.GetString(5))!);
        var budget = RunBudget.FromJson(JsonNode.Parse(reader.GetString(6))!);
        var overlap = AutomationStorageMaps.OverlapFromStorage(reader.GetString(7));
        var missed = AutomationStorageMaps.MissedFromStorage(reader.GetString(8));
        var permission = PermissionRequest.FromJson(JsonNode.Parse(reader.GetString(9))!);
        return new AutomationRevision(
            AutomationRevisionId.Parse(reader.GetString(0)),
            AutomationId.Parse(reader.GetString(1)),
            reader.GetInt32(2),
            trigger, workflow, binding, budget, overlap, missed, permission,
            reader.GetString(10),
            DateTimeOffset.Parse(reader.GetString(11), CultureInfo.InvariantCulture));
    }

    private async Task<long?> GetRowVersionAsync(AutomationId id, DbTransaction tx, CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText = "SELECT row_version FROM automation_definitions WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id.Value);
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is null or DBNull ? null : (long?)Convert.ToInt64(v);
    }
}
