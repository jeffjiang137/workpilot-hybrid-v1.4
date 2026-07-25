using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WorkPilot.Models;

namespace WorkPilot.Services;

public sealed class ConnectorService : IDisposable
{
    private readonly DatabaseService _database;
    private readonly SecretService _secrets;
    private readonly HttpClient _http;
    private readonly ConnectorExecutionGate _executionGate = new();

    public ConnectorService(DatabaseService database, SecretService secrets, HttpMessageHandler? handler = null)
    {
        _database = database; _secrets = secrets;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<IReadOnlyList<ConnectorAccount>> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<ConnectorAccount>();
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.id,d.kind,a.display_name,a.identity_summary,a.credential_ref,a.granted_scopes_json,
                   a.state,a.last_success_at_utc,a.last_error_code,a.row_version
            FROM connector_accounts a JOIN connector_definitions d ON d.id=a.connector_definition_id
            ORDER BY a.updated_at_utc DESC LIMIT 100
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(new(reader.GetString(0), reader.GetString(1),
            reader.GetString(2), reader.GetString(3), reader.GetString(4),
            JsonSerializer.Deserialize<List<string>>(reader.GetString(5)) ?? [], reader.GetString(6),
            reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7)),
            reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetInt64(9)));
        return result;
    }

    public async Task<ConnectorAccount> ConnectAsync(string kind, string displayName, string token,
        string spaceId, CancellationToken cancellationToken = default)
    {
        if (kind is not ("github" or "notion")) throw new ArgumentException("不支持的连接器类型");
        if (displayName.Trim().Length is < 1 or > 80 || token.Trim().Length is < 8 or > 8192)
            throw new ArgumentException("连接名称或 token 长度无效");
        var identity = await TestTokenAsync(kind, token.Trim(), cancellationToken);
        var id = Guid.NewGuid().ToString("N"); var credentialRef = Guid.NewGuid().ToString("N");
        _secrets.SaveCredential(credentialRef, new Dictionary<string, string> { ["token"] = token.Trim() });
        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var now = DateTimeOffset.UtcNow.ToString("O"); var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO connector_accounts(id,connector_definition_id,display_name,identity_summary,
                  credential_ref,granted_scopes_json,state,last_success_at_utc,last_error_code,created_at_utc,updated_at_utc,row_version)
                VALUES($id,$definition,$name,$identity,$credential,'[]','connected',$now,NULL,$now,$now,1)
                """;
            command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$definition", "builtin-" + kind);
            command.Parameters.AddWithValue("$name", displayName.Trim()); command.Parameters.AddWithValue("$identity", identity);
            command.Parameters.AddWithValue("$credential", credentialRef); command.Parameters.AddWithValue("$now", now);
            await command.ExecuteNonQueryAsync(cancellationToken);
            var space = connection.CreateCommand(); space.Transaction = (SqliteTransaction)transaction;
            space.CommandText = "INSERT INTO space_connectors VALUES($space,$account,1,'{}',$now,$now)";
            space.Parameters.AddWithValue("$space", spaceId); space.Parameters.AddWithValue("$account", id);
            space.Parameters.AddWithValue("$now", now); await space.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (await ListAsync(cancellationToken)).Single(x => x.Id == id);
        }
        catch { _secrets.DeleteCredential(credentialRef); throw; }
    }

    public async Task<string> TestAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var account = (await ListAsync(cancellationToken)).SingleOrDefault(x => x.Id == accountId)
            ?? throw new KeyNotFoundException("连接不存在");
        using var lease = _secrets.OpenCredential(account.CredentialRef);
        try
        {
            var identity = await TestTokenAsync(account.Kind, lease.GetRequired("token"), cancellationToken);
            await SetStateAsync(account.Id, "connected", null, identity, cancellationToken); return identity;
        }
        catch (HttpRequestException error)
        {
            await SetStateAsync(account.Id, "degraded", "Network", null, cancellationToken); throw new InvalidOperationException("连接测试失败，请检查网络或凭据", error);
        }
    }

    public async Task SetEnabledAsync(string accountId, bool enabled, CancellationToken cancellationToken = default) =>
        await SetStateAsync(accountId, enabled ? "connected" : "disabled", null, null, cancellationToken);

    public async Task DeleteAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var account = (await ListAsync(cancellationToken)).SingleOrDefault(x => x.Id == accountId)
            ?? throw new KeyNotFoundException("连接不存在");
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand(); command.CommandText = "DELETE FROM connector_accounts WHERE id=$id";
        command.Parameters.AddWithValue("$id", accountId); await command.ExecuteNonQueryAsync(cancellationToken);
        _secrets.DeleteCredential(account.CredentialRef);
    }

    public async Task SetSpaceEnabledAsync(string accountId, string spaceId, bool enabled,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand(); command.CommandText = """
            INSERT INTO space_connectors(space_id,connector_account_id,enabled,policy_json,created_at_utc,updated_at_utc)
            VALUES($space,$account,$enabled,'{}',$now,$now)
            ON CONFLICT(space_id,connector_account_id) DO UPDATE SET enabled=$enabled,updated_at_utc=$now
            """;
        command.Parameters.AddWithValue("$space", spaceId); command.Parameters.AddWithValue("$account", accountId);
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0); command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<(ConnectorAccount Account, ConnectorCapability Capability)>>
        GetAvailableCapabilitiesAsync(string spaceId, string expertId, CancellationToken cancellationToken = default)
    {
        var accounts = await ListAsync(cancellationToken);
        var allowed = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand(); command.CommandText = """
            SELECT a.id,e.allowed_capabilities_json FROM connector_accounts a
            JOIN space_connectors s ON s.connector_account_id=a.id AND s.space_id=$space AND s.enabled=1
            JOIN expert_connector_grants e ON e.connector_account_id=a.id AND e.expert_id=$expert AND e.enabled=1
            WHERE a.state IN('connected','degraded')
            """;
        command.Parameters.AddWithValue("$space", spaceId); command.Parameters.AddWithValue("$expert", expertId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var json = reader.GetString(1);
            if (json.Length > 128 * 1024) throw new InvalidDataException("连接器能力授权数据超过上限");
            allowed[reader.GetString(0)] = JsonSerializer.Deserialize<HashSet<string>>(json) ?? [];
        }
        return accounts.Where(x => allowed.ContainsKey(x.Id))
            .SelectMany(account => ConnectorRegistry.Get(account.Kind)
                .Where(capability => allowed[account.Id].Contains(capability.StableId))
                .Select(capability => (account, capability))).ToList();
    }

    public async Task<CapabilityResult> InvokeAsync(string accountId, string stableId, JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var account = (await ListAsync(cancellationToken)).SingleOrDefault(x => x.Id == accountId)
            ?? throw new KeyNotFoundException("连接不存在");
        var capability = ConnectorRegistry.Get(account.Kind).SingleOrDefault(x => x.StableId == stableId)
            ?? throw new ArgumentException("连接器能力不存在");
        using var lease = _secrets.OpenCredential(account.CredentialRef);
        var started = DateTimeOffset.UtcNow;
        try
        {
            var token = lease.GetRequired("token");
            var result = await _executionGate.ExecuteAsync(account.Id, capability.Mutating, async ct =>
                account.Kind == "github"
                    ? await InvokeGitHubAsync(token, capability.Operation, arguments, ct)
                    : await InvokeNotionAsync(token, capability.Operation, arguments, ct), cancellationToken);
            await SetStateAsync(account.Id, "connected", null, null, cancellationToken); return result;
        }
        catch (ConnectorHttpException error) when (error.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            await SetStateAsync(account.Id, "expired", "Authentication", null, cancellationToken); throw;
        }
        finally { AppLogger.Info($"Connector invocation {stableId} finished in {(DateTimeOffset.UtcNow - started).TotalMilliseconds:F0}ms"); }
    }

    private async Task<string> TestTokenAsync(string kind, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, kind == "github"
            ? "https://api.github.com/user" : "https://api.notion.com/v1/users/me");
        AddHeaders(request, kind, token); using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new ConnectorHttpException(response.StatusCode, "凭据无效或权限不足");
        using var json = JsonDocument.Parse(await ReadBoundedAsync(response, 256 * 1024, cancellationToken));
        if (kind == "github") return GetString(json.RootElement, "login", "GitHub 用户");
        var name = GetString(json.RootElement, "name", "Notion integration");
        return name.Length > 120 ? name[..120] : name;
    }

    private async Task SetStateAsync(string id, string state, string? error, string? identity,
        CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand(); command.CommandText = """
            UPDATE connector_accounts SET state=$state,last_error_code=$error,
              identity_summary=COALESCE($identity,identity_summary),last_success_at_utc=CASE WHEN $state='connected' THEN $now ELSE last_success_at_utc END,
              updated_at_utc=$now,row_version=row_version+1 WHERE id=$id
            """;
        command.Parameters.AddWithValue("$state", state); command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$identity", (object?)identity ?? DBNull.Value); command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", id); await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<CapabilityResult> InvokeGitHubAsync(string token, string operation,
        JsonElement args, CancellationToken cancellationToken)
    {
        var owner = Optional(args, "owner"); var repo = Optional(args, "repo");
        var endpoint = operation switch
        {
            "search_repositories" => "/search/repositories?q=" + Uri.EscapeDataString(Required(args, "query")) + "&per_page=50",
            "get_repository" => $"/repos/{Segment(owner)}/{Segment(repo)}",
            "list_issues" => $"/repos/{Segment(owner)}/{Segment(repo)}/issues?per_page=100",
            "get_issue" => $"/repos/{Segment(owner)}/{Segment(repo)}/issues/{Number(args)}",
            "list_pull_requests" => $"/repos/{Segment(owner)}/{Segment(repo)}/pulls?per_page=100",
            "get_pull_request" => $"/repos/{Segment(owner)}/{Segment(repo)}/pulls/{Number(args)}",
            "get_file_text" => $"/repos/{Segment(owner)}/{Segment(repo)}/contents/{PathSegments(Required(args, "path"))}" +
                (Optional(args, "ref") is { Length: > 0 } reference ? "?ref=" + Uri.EscapeDataString(reference) : ""),
            "create_issue" => $"/repos/{Segment(owner)}/{Segment(repo)}/issues",
            "add_issue_comment" => $"/repos/{Segment(owner)}/{Segment(repo)}/issues/{Number(args)}/comments",
            _ => throw new ArgumentException("未知 GitHub 操作")
        };
        var method = operation is "create_issue" or "add_issue_comment" ? HttpMethod.Post : HttpMethod.Get;
        using var request = new HttpRequestMessage(method, "https://api.github.com" + endpoint); AddHeaders(request, "github", token);
        if (operation == "create_issue") request.Content = Json(new { title = Required(args, "title"), body = Optional(args, "body") ?? "" });
        if (operation == "add_issue_comment") request.Content = Json(new { body = Required(args, "body") });
        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new ConnectorHttpException(response.StatusCode, $"GitHub 请求失败：{(int)response.StatusCode}");
        var text = await ReadBoundedAsync(response, operation == "get_file_text" ? 1024 * 1024 : 5 * 1024 * 1024, cancellationToken);
        if (operation == "get_file_text") text = DecodeGitHubFile(text);
        return BoundedResult(text);
    }

    private async Task<CapabilityResult> InvokeNotionAsync(string token, string operation,
        JsonElement args, CancellationToken cancellationToken)
    {
        var endpoint = operation switch
        {
            "search" => "/v1/search", "get_page" => "/v1/pages/" + NotionId(Required(args, "page_id")),
            "get_database" => "/v1/databases/" + NotionId(Required(args, "database_id")),
            "query_database" => "/v1/databases/" + NotionId(Required(args, "database_id")) + "/query",
            "append_page_blocks" => "/v1/blocks/" + NotionId(Required(args, "page_id")) + "/children",
            _ => throw new ArgumentException("未知 Notion 操作")
        };
        var method = operation is "get_page" or "get_database" ? HttpMethod.Get : HttpMethod.Post;
        using var request = new HttpRequestMessage(method, "https://api.notion.com" + endpoint); AddHeaders(request, "notion", token);
        if (operation == "search") request.Content = Json(new { query = Required(args, "query"), page_size = 100 });
        else if (operation == "query_database") request.Content = Json(new { page_size = 100 });
        else if (operation == "append_page_blocks")
        {
            var text = Required(args, "text"); if (text.Length > 20_000) throw new ArgumentException("追加内容超过 20,000 字符");
            request.Content = Json(new { children = new[] { new { @object = "block", type = "paragraph",
                paragraph = new { rich_text = new[] { new { type = "text", text = new { content = text } } } } } } });
        }
        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new ConnectorHttpException(response.StatusCode, $"Notion 请求失败：{(int)response.StatusCode}");
        return BoundedResult(await ReadBoundedAsync(response, 5 * 1024 * 1024, cancellationToken));
    }

    private static void AddHeaders(HttpRequestMessage request, string kind, string token)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (kind == "github") { request.Headers.UserAgent.ParseAdd("WorkPilot/1.4"); request.Headers.Accept.ParseAdd("application/vnd.github+json"); }
        else request.Headers.Add("Notion-Version", "2022-06-28");
    }

    private static StringContent Json(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
    private static string Required(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! : throw new ArgumentException($"缺少参数：{name}");
    private static string? Optional(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int Number(JsonElement root) => root.TryGetProperty("number", out var value) && value.TryGetInt32(out var number) && number > 0 ? number : throw new ArgumentException("number 必须为正整数");
    private static string Segment(string? value) => Uri.EscapeDataString(!string.IsNullOrWhiteSpace(value) && value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.') ? value : throw new ArgumentException("owner/repo 格式无效"));
    private static string PathSegments(string value)
    {
        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(x => x is "." or ".."))
            throw new ArgumentException("GitHub 文件路径无效");
        return string.Join('/', segments.Select(Segment));
    }
    private static string NotionId(string value) => Guid.TryParse(value, out var id) ? id.ToString("D") : throw new ArgumentException("Notion ID 无效");
    private static string GetString(JsonElement root, string name, string fallback) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
    private static string DecodeGitHubFile(string json) { using var document = JsonDocument.Parse(json); var root = document.RootElement; if (GetString(root, "encoding", "") != "base64") throw new InvalidDataException("GitHub 文件不是可读取文本"); var bytes = Convert.FromBase64String(GetString(root, "content", "").ReplaceLineEndings("")); if (bytes.Length > 1024 * 1024 || bytes.Contains((byte)0)) throw new InvalidDataException("GitHub 文件为二进制或超过 1 MiB"); return Encoding.UTF8.GetString(bytes); }
    private static CapabilityResult BoundedResult(string text) { var truncated = text.Length > 20_000; return new(true, truncated ? text[..20_000] + "\n[结果已截断]" : text, IsTruncated: truncated); }

    private static async Task<string> ReadBoundedAsync(HttpResponseMessage response, int maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken); using var output = new MemoryStream();
        var buffer = new byte[81920]; while (true) { var count = await stream.ReadAsync(buffer, cancellationToken); if (count == 0) break; if (output.Length + count > maxBytes) throw new InvalidDataException("外部响应超过大小上限"); await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken); }
        return Encoding.UTF8.GetString(output.ToArray());
    }

    public void Dispose() => _http.Dispose();
}

public sealed class ConnectorHttpException(HttpStatusCode statusCode, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}
