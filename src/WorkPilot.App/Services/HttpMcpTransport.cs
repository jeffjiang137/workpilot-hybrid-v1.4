using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace WorkPilot.Services;

public sealed class HttpMcpTransport(string endpoint, bool localMode, string? bearerToken) : IMcpTransport
{
    private const int MaxMessageBytes = 4 * 1024 * 1024;
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(90) };
    private Uri? _endpoint; private string? _sessionId;

    public async Task StartAsync(CancellationToken cancellationToken) =>
        _endpoint = await McpEndpointPolicy.ValidateAsync(endpoint, localMode, cancellationToken);

    public async Task<JsonElement> RequestAsync(long id, string method, object? parameters,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = id.ToString(), method, @params = parameters });
        var root = await SendAsync(payload, allowEmpty: false, cancellationToken)
            ?? throw new McpProtocolException("MCP HTTP response 为空");
        if (root.TryGetProperty("error", out var error)) throw new McpProtocolException("MCP 错误：" + Limit(error.ToString(), 800));
        if (!root.TryGetProperty("result", out var result)) throw new McpProtocolException("MCP HTTP response 缺少 result");
        return result.Clone();
    }

    public async Task NotifyAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params = parameters });
        await SendAsync(payload, allowEmpty: true, cancellationToken);
    }

    private async Task<JsonElement?> SendAsync(string payload, bool allowEmpty,
        CancellationToken cancellationToken)
    {
        if (_endpoint is null) throw new InvalidOperationException("MCP HTTP transport 未启动");
        Uri current = _endpoint;
        for (var redirects = 0; redirects <= 3; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, current);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            request.Headers.Add("Origin", "app://workpilot");
            if (_sessionId is not null) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
            if (!string.IsNullOrWhiteSpace(bearerToken) && current.Host == _endpoint.Host)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                if (redirects == 3 || response.Headers.Location is null) throw new McpProtocolException("MCP HTTP 重定向超过 3 次");
                var next = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(current, response.Headers.Location);
                if (current.Scheme == Uri.UriSchemeHttps && next.Scheme != Uri.UriSchemeHttps) throw new McpProtocolException("MCP 重定向试图从 HTTPS 降级");
                current = await McpEndpointPolicy.ValidateAsync(next.ToString(), localMode, cancellationToken); continue;
            }
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"MCP HTTP 返回 {(int)response.StatusCode}");
            if (response.Headers.TryGetValues("Mcp-Session-Id", out var sessions)) _sessionId = sessions.FirstOrDefault();
            if (allowEmpty && (response.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.NoContent ||
                response.Content.Headers.ContentLength == 0)) return null;
            var media = response.Content.Headers.ContentType?.MediaType;
            if (string.Equals(media, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await foreach (var data in SseParser.ReadDataAsync(stream, cancellationToken, MaxMessageBytes))
                    if (!string.IsNullOrWhiteSpace(data)) return Parse(data);
                throw new McpProtocolException("MCP SSE 未返回 JSON-RPC 消息");
            }
            var body = await ReadBoundedAsync(response, cancellationToken);
            if (allowEmpty && string.IsNullOrWhiteSpace(body)) return null;
            return Parse(body);
        }
        throw new McpProtocolException("MCP HTTP 请求失败");
    }

    private static JsonElement Parse(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxMessageBytes) throw new McpProtocolException("MCP 消息超过 4 MiB");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        return document.RootElement.Clone();
    }

    private static async Task<string> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken); using var output = new MemoryStream();
        var buffer = new byte[81920]; while (true) { var count = await input.ReadAsync(buffer, cancellationToken); if (count == 0) break; if (output.Length + count > MaxMessageBytes) throw new McpProtocolException("MCP 消息超过 4 MiB"); await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken); }
        return Encoding.UTF8.GetString(output.ToArray());
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_endpoint is null || _sessionId is null) return;
        try { using var request = new HttpRequestMessage(HttpMethod.Delete, _endpoint); request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId); using var _ = await _http.SendAsync(request, cancellationToken); }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException) { AppLogger.Error("MCP session delete failed", error); }
        _sessionId = null;
    }

    public async ValueTask DisposeAsync() { await StopAsync(CancellationToken.None); _http.Dispose(); }
    private static string Limit(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}
