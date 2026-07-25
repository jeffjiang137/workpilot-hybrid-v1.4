using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WorkPilot.Models;

namespace WorkPilot.Services;

public sealed class ExpertService(DatabaseService database)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<Expert>> ListAsync(bool includeArchived = false,
        CancellationToken cancellationToken = default)
    {
        var result = new List<Expert>();
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.id,e.name,e.description,e.color_key,e.status,e.current_revision_id,
                   r.revision_number,r.model_preference,e.created_at_utc,e.updated_at_utc,e.row_version,
                   (SELECT COUNT(*) FROM expert_skills es WHERE es.expert_id=e.id AND es.enabled=1),
                   (SELECT COUNT(*) FROM expert_connector_grants ec WHERE ec.expert_id=e.id AND ec.enabled=1)+
                   (SELECT COUNT(*) FROM expert_mcp_grants em WHERE em.expert_id=e.id AND em.enabled=1)
            FROM experts e JOIN expert_revisions r ON r.id=e.current_revision_id
            WHERE ($archived=1 OR e.status='active') ORDER BY e.is_builtin DESC,e.updated_at_utc DESC LIMIT 200
            """;
        command.Parameters.AddWithValue("$archived", includeArchived ? 1 : 0);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadExpert(reader));
        return result;
    }

    public async Task<Expert?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.id,e.name,e.description,e.color_key,e.status,e.current_revision_id,
                   r.revision_number,r.model_preference,e.created_at_utc,e.updated_at_utc,e.row_version,
                   (SELECT COUNT(*) FROM expert_skills es WHERE es.expert_id=e.id AND es.enabled=1),
                   (SELECT COUNT(*) FROM expert_connector_grants ec WHERE ec.expert_id=e.id AND ec.enabled=1)+
                   (SELECT COUNT(*) FROM expert_mcp_grants em WHERE em.expert_id=e.id AND em.enabled=1)
            FROM experts e JOIN expert_revisions r ON r.id=e.current_revision_id WHERE e.id=$id
            """;
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadExpert(reader) : null;
    }

    public async Task<ExpertRevision> GetCurrentRevisionAsync(string expertId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.id,r.expert_id,r.revision_number,r.model_preference,r.system_instruction,
                   r.capability_policy_json,r.snapshot_json,r.snapshot_sha256,r.created_at_utc
            FROM expert_revisions r JOIN experts e ON e.current_revision_id=r.id WHERE e.id=$id
            """;
        command.Parameters.AddWithValue("$id", expertId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new KeyNotFoundException("专家不存在或修订缺失");
        return ReadRevision(reader);
    }

    public async Task<IReadOnlyList<ExpertRevision>> GetRevisionsAsync(string expertId,
        CancellationToken cancellationToken = default)
    {
        var result = new List<ExpertRevision>();
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,expert_id,revision_number,model_preference,system_instruction,
                   capability_policy_json,snapshot_json,snapshot_sha256,created_at_utc
            FROM expert_revisions WHERE expert_id=$id ORDER BY revision_number DESC LIMIT 20
            """;
        command.Parameters.AddWithValue("$id", expertId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadRevision(reader));
        return result;
    }

    public async Task<ExpertDraft> GetDraftAsync(string expertId, CancellationToken cancellationToken = default)
    {
        var revision = await GetCurrentRevisionAsync(expertId, cancellationToken);
        using var snapshot = JsonDocument.Parse(revision.SnapshotJson); var root = snapshot.RootElement;
        static List<string> ReadIds(JsonElement value, string name) => value.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList() : [];
        var expert = await GetAsync(expertId, cancellationToken) ?? throw new KeyNotFoundException("专家不存在");
        return new(expert.Name, expert.Description, expert.ColorKey, revision.ModelPreference,
            revision.SystemInstruction, ReadIds(root, "skills"), ReadIds(root, "connectors"),
            ReadIds(root, "mcpServers"), RiskLevel.High, ReadIds(root, "automaticSkills"));
    }

    public async Task<Expert> CreateAsync(ExpertDraft draft, CancellationToken cancellationToken = default)
    {
        Validate(draft); var now = DateTimeOffset.UtcNow; var id = Guid.NewGuid().ToString("N");
        var revisionId = Guid.NewGuid().ToString("N"); var snapshot = BuildSnapshot(draft);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await InsertExpertAsync(connection, (SqliteTransaction)transaction, id, revisionId, draft,
            snapshot, now, cancellationToken);
        await ReplaceBindingsAsync(connection, (SqliteTransaction)transaction, id, draft, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await GetAsync(id, cancellationToken))!;
    }

    public async Task<Expert> UpdateAsync(Expert current, ExpertDraft draft,
        CancellationToken cancellationToken = default)
    {
        Validate(draft); var snapshot = BuildSnapshot(draft); var hash = Sha256(snapshot);
        var existing = await GetCurrentRevisionAsync(current.Id, cancellationToken);
        if (existing.SnapshotSha256 == hash) return current;
        var now = DateTimeOffset.UtcNow; var revisionId = Guid.NewGuid().ToString("N");
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var revision = connection.CreateCommand(); revision.Transaction = (SqliteTransaction)transaction;
        revision.CommandText = """
            INSERT INTO expert_revisions(id,expert_id,revision_number,model_preference,system_instruction,
              capability_policy_json,snapshot_json,snapshot_sha256,created_at_utc)
            VALUES($rid,$id,$number,$model,$instruction,$policy,$snapshot,$hash,$now)
            """;
        BindRevision(revision, revisionId, current.Id, current.RevisionNumber + 1, draft, snapshot, hash, now);
        await revision.ExecuteNonQueryAsync(cancellationToken);
        var update = connection.CreateCommand(); update.Transaction = (SqliteTransaction)transaction;
        update.CommandText = """
            UPDATE experts SET name=$name,description=$description,color_key=$color,current_revision_id=$revision,
              updated_at_utc=$now,row_version=row_version+1
            WHERE id=$id AND row_version=$rowVersion
            """;
        update.Parameters.AddWithValue("$name", draft.Name.Trim()); update.Parameters.AddWithValue("$description", draft.Description.Trim());
        update.Parameters.AddWithValue("$color", draft.ColorKey); update.Parameters.AddWithValue("$revision", revisionId);
        update.Parameters.AddWithValue("$now", now.ToString("O")); update.Parameters.AddWithValue("$id", current.Id);
        update.Parameters.AddWithValue("$rowVersion", current.RowVersion);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("专家已被其他窗口修改，请重新加载后再保存");
        await ReplaceBindingsAsync(connection, (SqliteTransaction)transaction, current.Id, draft, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await GetAsync(current.Id, cancellationToken))!;
    }

    public async Task ArchiveAsync(Expert expert, bool archived, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var builtin = connection.CreateCommand(); builtin.CommandText = "SELECT is_builtin FROM experts WHERE id=$id";
        builtin.Parameters.AddWithValue("$id", expert.Id);
        if (archived && Convert.ToInt32(await builtin.ExecuteScalarAsync(cancellationToken)) == 1)
            throw new InvalidOperationException("默认助手不能归档");
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE experts SET status=$status,archived_at_utc=$archived,updated_at_utc=$now,row_version=row_version+1
            WHERE id=$id AND row_version=$version
            """;
        command.Parameters.AddWithValue("$status", archived ? "archived" : "active");
        command.Parameters.AddWithValue("$archived", archived ? DateTimeOffset.UtcNow.ToString("O") : DBNull.Value);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); command.Parameters.AddWithValue("$id", expert.Id);
        command.Parameters.AddWithValue("$version", expert.RowVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("专家状态已变化，请重新加载");
    }

    private static async Task InsertExpertAsync(SqliteConnection connection, SqliteTransaction transaction,
        string id, string revisionId, ExpertDraft draft, string snapshot, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var expert = connection.CreateCommand(); expert.Transaction = transaction;
        expert.CommandText = """
            INSERT INTO experts(id,name,description,color_key,current_revision_id,is_builtin,status,
              created_at_utc,updated_at_utc,row_version) VALUES($id,$name,$description,$color,$revision,0,'active',$now,$now,1)
            """;
        expert.Parameters.AddWithValue("$id", id); expert.Parameters.AddWithValue("$name", draft.Name.Trim());
        expert.Parameters.AddWithValue("$description", draft.Description.Trim()); expert.Parameters.AddWithValue("$color", draft.ColorKey);
        expert.Parameters.AddWithValue("$revision", revisionId); expert.Parameters.AddWithValue("$now", now.ToString("O"));
        await expert.ExecuteNonQueryAsync(cancellationToken);
        var revision = connection.CreateCommand(); revision.Transaction = transaction;
        revision.CommandText = """
            INSERT INTO expert_revisions(id,expert_id,revision_number,model_preference,system_instruction,
              capability_policy_json,snapshot_json,snapshot_sha256,created_at_utc)
            VALUES($rid,$id,$number,$model,$instruction,$policy,$snapshot,$hash,$now)
            """;
        BindRevision(revision, revisionId, id, 1, draft, snapshot, Sha256(snapshot), now);
        await revision.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void BindRevision(SqliteCommand command, string revisionId, string expertId,
        int number, ExpertDraft draft, string snapshot, string hash, DateTimeOffset now)
    {
        command.Parameters.AddWithValue("$rid", revisionId); command.Parameters.AddWithValue("$id", expertId);
        command.Parameters.AddWithValue("$number", number); command.Parameters.AddWithValue("$model", draft.ModelPreference.Trim());
        command.Parameters.AddWithValue("$instruction", draft.SystemInstruction.Trim());
        command.Parameters.AddWithValue("$policy", JsonSerializer.Serialize(new { maxRisk = draft.MaximumRisk.ToString().ToLowerInvariant() }));
        command.Parameters.AddWithValue("$snapshot", snapshot); command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
    }

    private static async Task ReplaceBindingsAsync(SqliteConnection connection, SqliteTransaction transaction,
        string expertId, ExpertDraft draft, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var delete = connection.CreateCommand(); delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM expert_skills WHERE expert_id=$id; DELETE FROM expert_connector_grants WHERE expert_id=$id; DELETE FROM expert_mcp_grants WHERE expert_id=$id";
        delete.Parameters.AddWithValue("$id", expertId); await delete.ExecuteNonQueryAsync(cancellationToken);
        for (var index = 0; index < draft.SkillVersionIds.Count; index++)
        {
            var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO expert_skills VALUES($expert,$skill,$sort,$mode,1,$now)";
            command.Parameters.AddWithValue("$expert", expertId); command.Parameters.AddWithValue("$skill", draft.SkillVersionIds[index]);
            command.Parameters.AddWithValue("$mode", draft.AutomaticSkillVersionIds?.Contains(
                draft.SkillVersionIds[index], StringComparer.Ordinal) == true ? "automatic" : "pinned");
            command.Parameters.AddWithValue("$sort", index); command.Parameters.AddWithValue("$now", now.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var accountId in draft.ConnectorAccountIds.Distinct(StringComparer.Ordinal))
            await InsertConnectorGrantAsync(connection, transaction, expertId, accountId, now, cancellationToken);
        foreach (var serverId in draft.McpServerIds.Distinct(StringComparer.Ordinal))
            await InsertMcpGrantAsync(connection, transaction, expertId, serverId, now, cancellationToken);
    }

    private static async Task InsertConnectorGrantAsync(SqliteConnection connection,
        SqliteTransaction transaction, string expertId, string accountId, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var kindQuery = connection.CreateCommand(); kindQuery.Transaction = transaction;
        kindQuery.CommandText = """
            SELECT d.kind FROM connector_accounts a JOIN connector_definitions d
              ON d.id=a.connector_definition_id WHERE a.id=$id
            """;
        kindQuery.Parameters.AddWithValue("$id", accountId);
        var kind = await kindQuery.ExecuteScalarAsync(cancellationToken) as string
            ?? throw new InvalidOperationException("连接器账号不存在");
        var allowed = ConnectorRegistry.Get(kind).Select(x => x.StableId).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO expert_connector_grants(expert_id,connector_account_id,allowed_capabilities_json,
              enabled,created_at_utc,updated_at_utc) VALUES($expert,$source,$allowed,1,$now,$now)
            """;
        command.Parameters.AddWithValue("$expert", expertId); command.Parameters.AddWithValue("$source", accountId);
        command.Parameters.AddWithValue("$allowed", JsonSerializer.Serialize(allowed));
        command.Parameters.AddWithValue("$now", now.ToString("O")); await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertMcpGrantAsync(SqliteConnection connection, SqliteTransaction transaction,
        string expertId, string serverId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var ids = new List<string>(); var query = connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT id FROM mcp_capabilities WHERE mcp_server_id=$server AND status='approved' ORDER BY id LIMIT 5000";
        query.Parameters.AddWithValue("$server", serverId);
        await using (var reader = await query.ExecuteReaderAsync(cancellationToken)) while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetString(0));
        var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO expert_mcp_grants(expert_id,mcp_server_id,allowed_capability_ids_json,enabled,created_at_utc,updated_at_utc) VALUES($expert,$server,$ids,1,$now,$now)";
        command.Parameters.AddWithValue("$expert", expertId); command.Parameters.AddWithValue("$server", serverId);
        command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(ids)); command.Parameters.AddWithValue("$now", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string BuildSnapshot(ExpertDraft draft) => JsonSerializer.Serialize(new
    {
        name = draft.Name.Trim(), description = draft.Description.Trim(), color = draft.ColorKey,
        model = draft.ModelPreference.Trim(), instruction = draft.SystemInstruction.Trim(),
        skills = draft.SkillVersionIds, connectors = draft.ConnectorAccountIds,
        automaticSkills = draft.AutomaticSkillVersionIds ?? [],
        mcpServers = draft.McpServerIds, maximumRisk = draft.MaximumRisk
    }, JsonOptions);

    private static void Validate(ExpertDraft draft)
    {
        var name = draft.Name.Trim();
        if (name.Length is < 1 or > 60 || name.Any(char.IsControl)) throw new ArgumentException("专家名称需为 1–60 个可见字符");
        if (draft.Description.Length > 400) throw new ArgumentException("专家描述不能超过 400 个字符");
        if (draft.SystemInstruction.Length > 32_000 || draft.SystemInstruction.Contains('\0')) throw new ArgumentException("系统指令无效或超过 32,000 字符");
        if (draft.SkillVersionIds.Count > 20) throw new ArgumentException("一个专家最多绑定 20 个技能");
        if ((draft.AutomaticSkillVersionIds?.Except(draft.SkillVersionIds, StringComparer.Ordinal).Any() ?? false))
            throw new ArgumentException("自动技能必须属于当前专家已绑定技能");
        if (draft.ConnectorAccountIds.Count + draft.McpServerIds.Count > 20) throw new ArgumentException("一个专家最多绑定 20 个连接来源");
    }

    private static Expert ReadExpert(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1),
        reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetInt32(6),
        reader.GetString(7), DateTimeOffset.Parse(reader.GetString(8)), DateTimeOffset.Parse(reader.GetString(9)),
        reader.GetInt64(10), reader.GetInt32(11), reader.GetInt32(12));

    private static ExpertRevision ReadRevision(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1),
        reader.GetInt32(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
        reader.GetString(7), DateTimeOffset.Parse(reader.GetString(8)));

    private static string Sha256(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
