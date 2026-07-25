using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WorkPilot.Models;

namespace WorkPilot.Services;

public sealed record McpInitializeResult(string ProtocolVersion, string ServerInfoJson);
public sealed record DiscoveredMcpCapability(string Kind, string Name, string Title,
    string Description, string SchemaJson, string AnnotationsJson, RiskLevel Risk, string SchemaSha256);

public sealed class McpProtocolClient(IMcpTransport transport) : IAsyncDisposable
{
    private static readonly string[] SupportedVersions = ["2025-11-25", "2025-06-18"];
    private long _nextId;
    public string? ProtocolVersion { get; private set; }

    public async Task<McpInitializeResult> InitializeAsync(CancellationToken cancellationToken)
    {
        await transport.StartAsync(cancellationToken);
        var result = await RequestAsync("initialize", new
        {
            protocolVersion = "2025-11-25",
            capabilities = new { elicitation = new { form = new { }, url = new { } } },
            clientInfo = new { name = "WorkPilot", version = "1.4.0" }
        }, cancellationToken);
        var version = result.TryGetProperty("protocolVersion", out var versionValue) ? versionValue.GetString() : null;
        if (version is null || !SupportedVersions.Contains(version, StringComparer.Ordinal))
            throw new McpProtocolException("MCP 协议不兼容。支持：2025-11-25、2025-06-18");
        ProtocolVersion = version;
        await transport.NotifyAsync("notifications/initialized", new { }, cancellationToken);
        var info = result.TryGetProperty("serverInfo", out var serverInfo) ? serverInfo.GetRawText() : "{}";
        return new(version, Limit(info, 32_000));
    }

    public async Task<IReadOnlyList<DiscoveredMcpCapability>> DiscoverAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized(); var result = new List<DiscoveredMcpCapability>();
        await ListToolsAsync(result, cancellationToken); await ListNamedAsync("resources/list", "resources", "resource", result, cancellationToken);
        await ListNamedAsync("prompts/list", "prompts", "prompt", result, cancellationToken); return result;
    }

    private async Task ListToolsAsync(List<DiscoveredMcpCapability> output, CancellationToken cancellationToken)
    {
        string? cursor = null; var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 0; page < 50; page++)
        {
            var response = await RequestAsync("tools/list", cursor is null ? new { } : new { cursor }, cancellationToken);
            if (!response.TryGetProperty("tools", out var items) || items.ValueKind != JsonValueKind.Array) return;
            foreach (var item in items.EnumerateArray().Take(5000 - output.Count))
            {
                var name = RequiredName(item); var schema = item.TryGetProperty("inputSchema", out var value) ? value.GetRawText() : "{}";
                if (Encoding.UTF8.GetByteCount(schema) > 32 * 1024) continue;
                var annotations = item.TryGetProperty("annotations", out var annotationValue) ? annotationValue.GetRawText() : "{}";
                output.Add(new("tool", name, Optional(item, "title") ?? name, Optional(item, "description") ?? "",
                    schema, Limit(annotations, 16_000), Classify(name, annotations), Sha256(schema + "\n" + annotations)));
            }
            cursor = Optional(response, "nextCursor"); if (string.IsNullOrEmpty(cursor)) return;
            if (!seen.Add(cursor)) throw new McpProtocolException("MCP tools/list cursor 循环");
        }
        throw new McpProtocolException("MCP tools/list 超过 50 页");
    }

    private async Task ListNamedAsync(string method, string collection, string kind,
        List<DiscoveredMcpCapability> output, CancellationToken cancellationToken)
    {
        try
        {
            string? cursor = null; var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var page = 0; page < 50; page++)
            {
                var response = await RequestAsync(method, cursor is null ? new { } : new { cursor }, cancellationToken);
                if (!response.TryGetProperty(collection, out var items) || items.ValueKind != JsonValueKind.Array) return;
                foreach (var item in items.EnumerateArray().Take(5000 - output.Count))
                {
                    var name = kind == "resource" ? Optional(item, "uri") ?? RequiredName(item) : RequiredName(item);
                    var schema = item.TryGetProperty("arguments", out var args) ? args.GetRawText() : "{}";
                    output.Add(new(kind, name, Optional(item, "title") ?? Optional(item, "name") ?? name,
                        Optional(item, "description") ?? "", Limit(schema, 32_000), "{}", RiskLevel.Medium, Sha256(schema)));
                }
                cursor = Optional(response, "nextCursor"); if (string.IsNullOrEmpty(cursor)) return;
                if (!seen.Add(cursor)) throw new McpProtocolException($"MCP {method} cursor 循环");
            }
        }
        catch (McpProtocolException error) when (error.Message.Contains("Method not found", StringComparison.OrdinalIgnoreCase))
            { AppLogger.Info($"MCP server does not support {method}"); }
    }

    public Task<JsonElement> CallToolAsync(string name, JsonElement arguments, CancellationToken cancellationToken) =>
        RequestAsync("tools/call", new { name, arguments }, cancellationToken);

    public Task<JsonElement> ReadResourceAsync(string uri, CancellationToken cancellationToken) =>
        RequestAsync("resources/read", new { uri }, cancellationToken);

    public Task<JsonElement> GetPromptAsync(string name, JsonElement arguments, CancellationToken cancellationToken) =>
        RequestAsync("prompts/get", new { name, arguments }, cancellationToken);

    private Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken cancellationToken) =>
        transport.RequestAsync(Interlocked.Increment(ref _nextId), method, parameters, cancellationToken);

    private void EnsureInitialized()
    {
        if (ProtocolVersion is null) throw new InvalidOperationException("MCP session 尚未初始化");
    }

    private static RiskLevel Classify(string name, string annotationsJson)
    {
        var lower = name.ToLowerInvariant();
        if (lower.Contains("delete") || lower.Contains("remove") || lower.Contains("execute") || lower.Contains("shell") || lower.Contains("payment")) return RiskLevel.Critical;
        if (lower.Contains("write") || lower.Contains("create") || lower.Contains("update") || lower.Contains("send") || lower.Contains("post")) return RiskLevel.High;
        try
        {
            using var document = JsonDocument.Parse(annotationsJson);
            var root = document.RootElement;
            if (root.TryGetProperty("destructiveHint", out var destructive) && destructive.ValueKind == JsonValueKind.True) return RiskLevel.Critical;
            if (root.TryGetProperty("readOnlyHint", out var readOnly) && readOnly.ValueKind == JsonValueKind.True) return RiskLevel.Medium;
        }
        catch (JsonException) { }
        return RiskLevel.High;
    }

    private static string RequiredName(JsonElement item) => Optional(item, "name") ?? throw new McpProtocolException("MCP capability 缺少 name");
    private static string? Optional(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Limit(string value, int max) => value.Length <= max ? value : value[..max] + "…";
    public async ValueTask DisposeAsync() => await transport.DisposeAsync();
}
