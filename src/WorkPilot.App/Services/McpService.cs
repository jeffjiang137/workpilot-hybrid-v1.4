using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WorkPilot.Models;

namespace WorkPilot.Services;

public sealed class McpService(DatabaseService database, SecretService secrets) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, McpProtocolClient> _sessions = new();

    public async Task<IReadOnlyList<McpServer>> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<McpServer>(); await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand(); command.CommandText = """
            SELECT id,display_name,transport_kind,config_json,credential_ref,enabled,state,negotiated_protocol,
                   capability_hash,last_connected_at_utc,last_error_code,row_version
            FROM mcp_servers ORDER BY updated_at_utc DESC LIMIT 100
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(new(reader.GetString(0), reader.GetString(1),
            reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt32(5) == 1,
            reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetString(8),
            reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9)), reader.IsDBNull(10) ? null : reader.GetString(10), reader.GetInt64(11)));
        return result;
    }

    public async Task<McpServer> AddAsync(McpServerDraft draft, string spaceId, string expertId,
        CancellationToken cancellationToken = default)
    {
        ValidateDraft(draft); var id = Guid.NewGuid().ToString("N"); string? credentialRef = null;
        if (!string.IsNullOrWhiteSpace(draft.BearerToken))
        {
            credentialRef = Guid.NewGuid().ToString("N"); secrets.SaveCredential(credentialRef,
                new Dictionary<string, string> { ["bearer_token"] = draft.BearerToken.Trim() });
        }
        var config = draft.TransportKind == "stdio"
            ? JsonSerializer.Serialize(new { executable = Path.GetFullPath(draft.Executable!), arguments = draft.Arguments, workingDirectory = draft.WorkingDirectory })
            : JsonSerializer.Serialize(new { endpoint = draft.Endpoint, localMode = draft.LocalMode });
        try
        {
            await using var connection = await database.OpenConnectionAsync(cancellationToken);
            var now = DateTimeOffset.UtcNow.ToString("O"); var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO mcp_servers(id,display_name,transport_kind,config_json,credential_ref,enabled,state,
                  negotiated_protocol,server_info_json,capability_hash,last_connected_at_utc,last_error_code,
                  created_at_utc,updated_at_utc,row_version)
                VALUES($id,$name,$transport,$config,$credential,1,'disconnected',NULL,'{}','',NULL,NULL,$now,$now,1)
                """;
            command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$name", draft.DisplayName.Trim());
            command.Parameters.AddWithValue("$transport", draft.TransportKind); command.Parameters.AddWithValue("$config", config);
            command.Parameters.AddWithValue("$credential", (object?)credentialRef ?? DBNull.Value); command.Parameters.AddWithValue("$now", now);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await ConnectAndDiscoverAsync(id, cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var space = connection.CreateCommand(); space.Transaction = (SqliteTransaction)transaction;
            space.CommandText = "INSERT INTO space_mcp_servers VALUES($space,$server,1,'{}',$now,$now)";
            space.Parameters.AddWithValue("$space", spaceId); space.Parameters.AddWithValue("$server", id); space.Parameters.AddWithValue("$now", now);
            await space.ExecuteNonQueryAsync(cancellationToken);
            var grant = connection.CreateCommand(); grant.Transaction = (SqliteTransaction)transaction;
            grant.CommandText = "INSERT INTO expert_mcp_grants VALUES($expert,$server,$capabilities,1,$now,$now)";
            grant.Parameters.AddWithValue("$expert", expertId); grant.Parameters.AddWithValue("$server", id);
            grant.Parameters.AddWithValue("$capabilities", "[]");
            grant.Parameters.AddWithValue("$now", now); await grant.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (await ListAsync(cancellationToken)).Single(x => x.Id == id);
        }
        catch
        {
            if (credentialRef is not null) secrets.DeleteCredential(credentialRef);
            await DeleteRowsAsync(id, cancellationToken); throw;
        }
    }

    public async Task<IReadOnlyList<McpCapability>> ConnectAndDiscoverAsync(string serverId,
        CancellationToken cancellationToken = default)
    {
        var server = (await ListAsync(cancellationToken)).SingleOrDefault(x => x.Id == serverId)
            ?? throw new KeyNotFoundException("MCP 服务不存在");
        await SetStateAsync(serverId, "starting", null, null, null, cancellationToken);
        try
        {
            var client = await CreateClientAsync(server, cancellationToken);
            var initialized = await client.InitializeAsync(cancellationToken);
            var discovered = await client.DiscoverAsync(cancellationToken);
            var saved = await SaveCapabilitiesAsync(server, initialized, discovered, cancellationToken);
            if (_sessions.TryRemove(serverId, out var previous)) await previous.DisposeAsync();
            _sessions[serverId] = client; return saved;
        }
        catch (Exception error)
        {
            await SetStateAsync(serverId, "error", null, null, error is TimeoutException ? "Timeout" : "Protocol", cancellationToken);
            throw;
        }
    }

    public async Task<IReadOnlyList<McpCapability>> GetCapabilitiesAsync(string serverId,
        CancellationToken cancellationToken = default)
    {
        var result = new List<McpCapability>(); await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand(); command.CommandText = """
            SELECT id,mcp_server_id,kind,remote_name,stable_name,title,description,input_schema_json,
                   annotations_json,local_risk,schema_sha256,status FROM mcp_capabilities
            WHERE mcp_server_id=$server AND status<>'removed' ORDER BY kind,title LIMIT 5000
            """;
        command.Parameters.AddWithValue("$server", serverId); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadCapability(reader)); return result;
    }

    public async Task ApproveCapabilitiesAsync(string serverId, IReadOnlyList<string> capabilityIds,
        CancellationToken cancellationToken = default)
    {
        if (capabilityIds.Count is < 1 or > 5000) return;
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var batch in capabilityIds.Distinct(StringComparer.Ordinal).Chunk(400))
        {
            var command = connection.CreateCommand(); command.Transaction = (SqliteTransaction)transaction;
            var names = batch.Select((_, index) => "$id" + index).ToArray();
            command.CommandText = $"UPDATE mcp_capabilities SET status='approved' WHERE mcp_server_id=$server AND status IN('discovered','stale') AND id IN({string.Join(',', names)})";
            command.Parameters.AddWithValue("$server", serverId);
            for (var index = 0; index < batch.Length; index++) command.Parameters.AddWithValue(names[index], batch[index]);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task GrantCapabilitiesAsync(string expertId, string serverId,
        IReadOnlyList<string> capabilityIds, CancellationToken cancellationToken = default)
    {
        var approved = (await GetCapabilitiesAsync(serverId, cancellationToken))
            .Where(x => capabilityIds.Contains(x.Id, StringComparer.Ordinal) && x.Status == "approved").Select(x => x.Id).ToList();
        await using var connection = await database.OpenConnectionAsync(cancellationToken); var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO expert_mcp_grants(expert_id,mcp_server_id,allowed_capability_ids_json,enabled,created_at_utc,updated_at_utc)
            VALUES($expert,$server,$ids,1,$now,$now)
            ON CONFLICT(expert_id,mcp_server_id) DO UPDATE SET allowed_capability_ids_json=$ids,enabled=1,updated_at_utc=$now
            """;
        command.Parameters.AddWithValue("$expert", expertId); command.Parameters.AddWithValue("$server", serverId);
        command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(approved)); command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<(McpServer Server, McpCapability Capability)>> GetAvailableCapabilitiesAsync(
        string spaceId, string expertId, CancellationToken cancellationToken = default)
    {
        var servers = await ListAsync(cancellationToken); var result = new List<(McpServer, McpCapability)>();
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        foreach (var server in servers.Where(x => x.Enabled && (x.State == "ready" || x.State == "degraded")))
        {
            var grant = connection.CreateCommand(); grant.CommandText = """
                SELECT e.allowed_capability_ids_json FROM expert_mcp_grants e
                JOIN space_mcp_servers s ON s.mcp_server_id=e.mcp_server_id AND s.space_id=$space AND s.enabled=1
                WHERE e.expert_id=$expert AND e.mcp_server_id=$server AND e.enabled=1
                """;
            grant.Parameters.AddWithValue("$space", spaceId); grant.Parameters.AddWithValue("$expert", expertId); grant.Parameters.AddWithValue("$server", server.Id);
            var json = await grant.ExecuteScalarAsync(cancellationToken) as string; if (json is null) continue;
            var allowed = JsonSerializer.Deserialize<HashSet<string>>(json) ?? [];
            result.AddRange((await GetCapabilitiesAsync(server.Id, cancellationToken)).Where(x => allowed.Contains(x.Id) && x.Status == "approved").Select(x => (server, x)));
        }
        return result;
    }

    public async Task<CapabilityResult> InvokeAsync(string serverId, string capabilityId,
        string expectedSchemaSha256, JsonElement arguments, CancellationToken cancellationToken)
    {
        var capability = (await GetCapabilitiesAsync(serverId, cancellationToken)).SingleOrDefault(x => x.Id == capabilityId)
            ?? throw new KeyNotFoundException("MCP 能力不存在");
        if (capability.Status != "approved" || capability.SchemaSha256 != expectedSchemaSha256)
            throw new InvalidOperationException("MCP 能力尚未批准或 Schema 已变化");
        if (!_sessions.TryGetValue(serverId, out var client))
        {
            await ConnectAndDiscoverAsync(serverId, cancellationToken);
            capability = (await GetCapabilitiesAsync(serverId, cancellationToken)).SingleOrDefault(x => x.Id == capabilityId)
                ?? throw new KeyNotFoundException("MCP 能力已移除");
            if (capability.Status != "approved" || capability.SchemaSha256 != expectedSchemaSha256)
                throw new InvalidOperationException("MCP 能力重连后发生变化，需要重新审查");
            client = _sessions[serverId];
        }
        JsonElement result = capability.Kind switch
        {
            "tool" => await client.CallToolAsync(capability.RemoteName, arguments, cancellationToken),
            "resource" => await client.ReadResourceAsync(capability.RemoteName, cancellationToken),
            "prompt" => await client.GetPromptAsync(capability.RemoteName, arguments, cancellationToken),
            _ => throw new InvalidOperationException("不支持的 MCP 能力类型")
        };
        var text = result.GetRawText(); var truncated = text.Length > 20_000;
        return new(true, truncated ? text[..20_000] + "\n[结果已截断]" : text, IsTruncated: truncated);
    }

    public async Task SetEnabledAsync(string serverId, bool enabled, CancellationToken cancellationToken = default)
    {
        if (!enabled && _sessions.TryRemove(serverId, out var session)) await session.DisposeAsync();
        await using var connection = await database.OpenConnectionAsync(cancellationToken); var command = connection.CreateCommand();
        command.CommandText = "UPDATE mcp_servers SET enabled=$enabled,state=$state,updated_at_utc=$now,row_version=row_version+1 WHERE id=$id";
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0); command.Parameters.AddWithValue("$state", enabled ? "disconnected" : "disabled");
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); command.Parameters.AddWithValue("$id", serverId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(string serverId, CancellationToken cancellationToken = default)
    {
        var server = (await ListAsync(cancellationToken)).SingleOrDefault(x => x.Id == serverId); if (server is null) return;
        if (_sessions.TryRemove(serverId, out var session)) await session.DisposeAsync();
        await DeleteRowsAsync(serverId, cancellationToken); if (server.CredentialRef is not null) secrets.DeleteCredential(server.CredentialRef);
    }

    private async Task<McpProtocolClient> CreateClientAsync(McpServer server, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(server.ConfigJson); var root = document.RootElement;
        if (server.TransportKind == "stdio") return new(new StdioMcpTransport(
            root.GetProperty("executable").GetString()!,
            root.GetProperty("arguments").EnumerateArray().Select(x => x.GetString() ?? "").ToList(),
            root.TryGetProperty("workingDirectory", out var cwd) && cwd.ValueKind == JsonValueKind.String ? cwd.GetString() : null));
        string? token = null;
        if (server.CredentialRef is not null) { using var lease = secrets.OpenCredential(server.CredentialRef); token = lease.GetRequired("bearer_token"); }
        var endpoint = root.GetProperty("endpoint").GetString()!; var local = root.TryGetProperty("localMode", out var localValue) && localValue.GetBoolean();
        await McpEndpointPolicy.ValidateAsync(endpoint, local, cancellationToken);
        return new(new HttpMcpTransport(endpoint, local, token));
    }

    private async Task<IReadOnlyList<McpCapability>> SaveCapabilitiesAsync(McpServer server,
        McpInitializeResult initialized, IReadOnlyList<DiscoveredMcpCapability> discovered, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.ToString("O"); await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var item in discovered.Take(5000))
        {
            var stable = StableName(server.Id, item.Name);
            var existing = connection.CreateCommand(); existing.Transaction = (SqliteTransaction)transaction;
            existing.CommandText = "SELECT id,schema_sha256,status FROM mcp_capabilities WHERE mcp_server_id=$server AND kind=$kind AND remote_name=$name";
            existing.Parameters.AddWithValue("$server", server.Id); existing.Parameters.AddWithValue("$kind", item.Kind); existing.Parameters.AddWithValue("$name", item.Name);
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken); string? id = null; string? oldHash = null; string? oldStatus = null;
            if (await reader.ReadAsync(cancellationToken)) { id = reader.GetString(0); oldHash = reader.GetString(1); oldStatus = reader.GetString(2); }
            await reader.DisposeAsync(); id ??= Guid.NewGuid().ToString("N");
            var status = oldHash is null ? "discovered" : oldHash == item.SchemaSha256 ? oldStatus == "removed" ? "discovered" : oldStatus : "stale";
            var command = connection.CreateCommand(); command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO mcp_capabilities(id,mcp_server_id,kind,remote_name,stable_name,title,description,
                  input_schema_json,annotations_json,local_risk,schema_sha256,status,discovered_at_utc)
                VALUES($id,$server,$kind,$name,$stable,$title,$description,$schema,$annotations,$risk,$hash,$status,$now)
                ON CONFLICT(mcp_server_id,kind,remote_name) DO UPDATE SET title=$title,description=$description,
                  input_schema_json=$schema,annotations_json=$annotations,local_risk=$risk,schema_sha256=$hash,status=$status,discovered_at_utc=$now
                """;
            command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$server", server.Id); command.Parameters.AddWithValue("$kind", item.Kind);
            command.Parameters.AddWithValue("$name", item.Name); command.Parameters.AddWithValue("$stable", stable); command.Parameters.AddWithValue("$title", Limit(item.Title, 200));
            command.Parameters.AddWithValue("$description", Limit(item.Description, 1000)); command.Parameters.AddWithValue("$schema", item.SchemaJson);
            command.Parameters.AddWithValue("$annotations", item.AnnotationsJson); command.Parameters.AddWithValue("$risk", (int)item.Risk);
            command.Parameters.AddWithValue("$hash", item.SchemaSha256); command.Parameters.AddWithValue("$status", status); command.Parameters.AddWithValue("$now", now);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        var removed = connection.CreateCommand(); removed.Transaction = (SqliteTransaction)transaction;
        removed.CommandText = "UPDATE mcp_capabilities SET status='removed' WHERE mcp_server_id=$server AND discovered_at_utc<>$now";
        removed.Parameters.AddWithValue("$server", server.Id); removed.Parameters.AddWithValue("$now", now); await removed.ExecuteNonQueryAsync(cancellationToken);
        var hash = Sha256(string.Join("\n", discovered.OrderBy(x => x.Kind).ThenBy(x => x.Name).Select(x => x.Kind + ":" + x.Name + ":" + x.SchemaSha256)));
        var update = connection.CreateCommand(); update.Transaction = (SqliteTransaction)transaction;
        update.CommandText = """
            UPDATE mcp_servers SET state='ready',negotiated_protocol=$protocol,server_info_json=$info,
              capability_hash=$hash,last_connected_at_utc=$now,last_error_code=NULL,updated_at_utc=$now,row_version=row_version+1 WHERE id=$id
            """;
        update.Parameters.AddWithValue("$protocol", initialized.ProtocolVersion); update.Parameters.AddWithValue("$info", initialized.ServerInfoJson);
        update.Parameters.AddWithValue("$hash", hash); update.Parameters.AddWithValue("$now", now); update.Parameters.AddWithValue("$id", server.Id);
        await update.ExecuteNonQueryAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
        return await GetCapabilitiesAsync(server.Id, cancellationToken);
    }

    private async Task SetStateAsync(string id, string state, string? protocol, string? info, string? error,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken); var command = connection.CreateCommand();
        command.CommandText = "UPDATE mcp_servers SET state=$state,negotiated_protocol=COALESCE($protocol,negotiated_protocol),server_info_json=COALESCE($info,server_info_json),last_error_code=$error,updated_at_utc=$now WHERE id=$id";
        command.Parameters.AddWithValue("$state", state); command.Parameters.AddWithValue("$protocol", (object?)protocol ?? DBNull.Value);
        command.Parameters.AddWithValue("$info", (object?)info ?? DBNull.Value); command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task DeleteRowsAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken); var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM mcp_servers WHERE id=$id"; command.Parameters.AddWithValue("$id", id); await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void ValidateDraft(McpServerDraft draft)
    {
        if (draft.DisplayName.Trim().Length is < 1 or > 80 || draft.TransportKind is not ("stdio" or "streamable_http")) throw new ArgumentException("MCP 名称或传输类型无效");
        if (draft.TransportKind == "stdio" && string.IsNullOrWhiteSpace(draft.Executable)) throw new ArgumentException("请选择 MCP executable");
        if (draft.TransportKind == "streamable_http" && string.IsNullOrWhiteSpace(draft.Endpoint)) throw new ArgumentException("请输入 MCP Endpoint");
        if ((draft.BearerToken?.Length ?? 0) > 8192 || draft.Arguments.Count > 64) throw new ArgumentException("MCP token 或参数超过上限");
    }

    private static McpCapability ReadCapability(Microsoft.Data.Sqlite.SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8), (RiskLevel)reader.GetInt32(9), reader.GetString(10), reader.GetString(11));
    private static string StableName(string serverId, string remoteName) { var safe = new string(remoteName.Select(x => char.IsAsciiLetterOrDigit(x) || x is '_' or '-' ? x : '_').ToArray()); if (safe.Length > 48) safe = safe[..48]; return $"mcp.{serverId[..8]}.{safe}_{Sha256(remoteName)[..6]}"; }
    private static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Limit(string value, int max) => value.Length <= max ? value : value[..max] + "…";

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values) await session.DisposeAsync(); _sessions.Clear();
    }
}
