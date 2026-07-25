using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WorkPilot.Services;

public sealed record McpOAuthToken(string AccessToken, string? RefreshToken, DateTimeOffset? ExpiresAt,
    IReadOnlyList<string> Scopes);

public sealed class McpOAuthService : IDisposable
{
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(30) };

    public async Task<McpOAuthToken> AuthenticateAsync(string resourceEndpoint, bool localMode,
        CancellationToken cancellationToken)
    {
        var resource = await McpEndpointPolicy.ValidateAsync(resourceEndpoint, localMode, cancellationToken);
        var protectedMetadata = await DiscoverProtectedResourceAsync(resource, localMode, cancellationToken);
        var authorizationServers = protectedMetadata.GetProperty("authorization_servers");
        if (authorizationServers.ValueKind != JsonValueKind.Array || authorizationServers.GetArrayLength() == 0)
            throw new InvalidOperationException("MCP Protected Resource Metadata 未声明 authorization server");
        var issuerText = authorizationServers[0].GetString() ?? throw new InvalidDataException("OAuth issuer 无效");
        var issuer = await McpEndpointPolicy.ValidateAsync(issuerText, localMode, cancellationToken);
        var metadata = await DiscoverAuthorizationServerAsync(issuer, localMode, cancellationToken);
        var authorizationEndpoint = await ValidateMetadataEndpointAsync(metadata, "authorization_endpoint", localMode, cancellationToken);
        var tokenEndpoint = await ValidateMetadataEndpointAsync(metadata, "token_endpoint", localMode, cancellationToken);
        var registrationEndpoint = await ValidateMetadataEndpointAsync(metadata, "registration_endpoint", localMode, cancellationToken);
        if (!metadata.TryGetProperty("code_challenge_methods_supported", out var methods) ||
            methods.ValueKind != JsonValueKind.Array || !methods.EnumerateArray().Any(x => x.GetString() == "S256"))
            throw new InvalidOperationException("OAuth 服务不支持 PKCE S256");

        var port = ReserveLoopbackPort(); var redirect = new Uri($"http://127.0.0.1:{port}/callback/");
        var clientId = await RegisterClientAsync(registrationEndpoint, redirect, cancellationToken);
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var scope = protectedMetadata.TryGetProperty("scopes_supported", out var scopes) && scopes.ValueKind == JsonValueKind.Array
            ? string.Join(' ', scopes.EnumerateArray().Take(20).Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x))) : "";
        var authorization = BuildUri(authorizationEndpoint, new Dictionary<string, string>
        {
            ["response_type"] = "code", ["client_id"] = clientId, ["redirect_uri"] = redirect.ToString(),
            ["code_challenge"] = challenge, ["code_challenge_method"] = "S256", ["state"] = state,
            ["resource"] = CanonicalResource(resource), ["scope"] = scope
        });
        using var listener = new HttpListener(); listener.Prefixes.Add(redirect.ToString()); listener.Start();
        if (!await Windows.System.Launcher.LaunchUriAsync(authorization)) throw new InvalidOperationException("无法打开系统浏览器完成 OAuth");
        HttpListenerContext callback;
        try { callback = await listener.GetContextAsync().WaitAsync(TimeSpan.FromMinutes(5), cancellationToken); }
        finally { listener.Stop(); }
        const string responseText = "WorkPilot authentication completed. You can close this window.";
        callback.Response.ContentType = "text/plain; charset=utf-8"; var responseBytes = Encoding.UTF8.GetBytes(responseText);
        await callback.Response.OutputStream.WriteAsync(responseBytes, cancellationToken); callback.Response.Close();
        if (!string.Equals(callback.Request.Url?.AbsolutePath, redirect.AbsolutePath, StringComparison.Ordinal))
            throw new InvalidOperationException("OAuth 回调路径不匹配");
        var query = callback.Request.QueryString;
        if (!string.Equals(query["state"], state, StringComparison.Ordinal)) throw new InvalidOperationException("OAuth state 不匹配");
        if (query["error"] is { } oauthError) throw new InvalidOperationException("OAuth 授权失败：" + oauthError);
        var code = query["code"] ?? throw new InvalidOperationException("OAuth 回调缺少 code");
        return await ExchangeTokenAsync(tokenEndpoint, clientId, redirect, code, verifier,
            CanonicalResource(resource), scope.Split(' ', StringSplitOptions.RemoveEmptyEntries), cancellationToken);
    }

    private async Task<JsonElement> DiscoverProtectedResourceAsync(Uri resource, bool localMode,
        CancellationToken cancellationToken)
    {
        using var probe = new HttpRequestMessage(HttpMethod.Get, resource);
        using var response = await _http.SendAsync(probe, cancellationToken);
        Uri? metadataUri = null;
        foreach (var challenge in response.Headers.WwwAuthenticate)
        {
            var parameter = challenge.Parameter ?? ""; const string key = "resource_metadata=\"";
            var start = parameter.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (start >= 0) { start += key.Length; var end = parameter.IndexOf('"', start); if (end > start && Uri.TryCreate(parameter[start..end], UriKind.Absolute, out var parsed)) metadataUri = parsed; }
        }
        metadataUri ??= new Uri(resource.GetLeftPart(UriPartial.Authority) + "/.well-known/oauth-protected-resource");
        await McpEndpointPolicy.ValidateAsync(metadataUri.ToString(), localMode, cancellationToken);
        var metadata = await GetJsonAsync(metadataUri, cancellationToken);
        if (metadata.TryGetProperty("resource", out var declaredResource) && declaredResource.ValueKind == JsonValueKind.String &&
            !string.Equals(declaredResource.GetString()?.TrimEnd('/'), CanonicalResource(resource), StringComparison.Ordinal))
            throw new InvalidOperationException("OAuth Protected Resource Metadata 的 resource 不匹配");
        return metadata;
    }

    private async Task<JsonElement> DiscoverAuthorizationServerAsync(Uri issuer, bool localMode,
        CancellationToken cancellationToken)
    {
        var path = issuer.AbsolutePath.Trim('/'); var metadata = new Uri(issuer.GetLeftPart(UriPartial.Authority) +
            "/.well-known/oauth-authorization-server" + (path.Length == 0 ? "" : "/" + path));
        await McpEndpointPolicy.ValidateAsync(metadata.ToString(), localMode, cancellationToken);
        var json = await GetJsonAsync(metadata, cancellationToken);
        if (!string.Equals(json.GetProperty("issuer").GetString()?.TrimEnd('/'), issuer.ToString().TrimEnd('/'), StringComparison.Ordinal))
            throw new InvalidOperationException("OAuth metadata issuer 不匹配");
        return json;
    }

    private async Task<string> RegisterClientAsync(Uri endpoint, Uri redirect, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                client_name = "WorkPilot Desktop", redirect_uris = new[] { redirect.ToString() },
                grant_types = new[] { "authorization_code", "refresh_token" },
                response_types = new[] { "code" }, token_endpoint_auth_method = "none"
            }), Encoding.UTF8, "application/json")
        };
        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("OAuth 动态客户端注册失败");
        var json = await ReadJsonAsync(response, cancellationToken);
        return json.GetProperty("client_id").GetString() ?? throw new InvalidDataException("OAuth 注册响应缺少 client_id");
    }

    private async Task<McpOAuthToken> ExchangeTokenAsync(Uri endpoint, string clientId, Uri redirect,
        string code, string verifier, string resource, IReadOnlyCollection<string> requestedScopes,
        CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["redirect_uri"] = redirect.ToString(),
            ["code"] = code, ["code_verifier"] = verifier, ["resource"] = resource
        };
        using var response = await _http.PostAsync(endpoint, new FormUrlEncodedContent(values), cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("OAuth token 交换失败");
        var json = await ReadJsonAsync(response, cancellationToken);
        if (!string.Equals(json.GetProperty("token_type").GetString(), "Bearer", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("OAuth token_type 不是 Bearer");
        var access = json.GetProperty("access_token").GetString() ?? throw new InvalidDataException("OAuth 响应缺少 access_token");
        var refresh = json.TryGetProperty("refresh_token", out var refreshValue) ? refreshValue.GetString() : null;
        DateTimeOffset? expires = json.TryGetProperty("expires_in", out var expiresValue) && expiresValue.TryGetInt32(out var seconds)
            ? DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(seconds, 1, 31_536_000)) : null;
        var scopes = json.TryGetProperty("scope", out var scopeValue) ? (scopeValue.GetString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries) : [];
        if (scopes.Except(requestedScopes, StringComparer.Ordinal).Any())
            throw new InvalidDataException("OAuth token scope 超出请求范围");
        if (json.TryGetProperty("resource", out var tokenResource) && tokenResource.ValueKind == JsonValueKind.String &&
            !string.Equals(tokenResource.GetString()?.TrimEnd('/'), resource, StringComparison.Ordinal))
            throw new InvalidDataException("OAuth token resource 不匹配");
        if (json.TryGetProperty("aud", out var audience) && !AudienceMatches(audience, resource))
            throw new InvalidDataException("OAuth token audience 不匹配");
        return new(access, refresh, expires, scopes);
    }

    private async Task<JsonElement> GetJsonAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(uri, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"OAuth metadata 请求失败：{(int)response.StatusCode}");
        return await ReadJsonAsync(response, cancellationToken);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length > 1024 * 1024) throw new InvalidDataException("OAuth 响应超过 1 MiB");
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 }); return document.RootElement.Clone();
    }

    private static async Task<Uri> ValidateMetadataEndpointAsync(JsonElement root, string name,
        bool localMode, CancellationToken cancellationToken)
    {
        var text = root.TryGetProperty(name, out var value) ? value.GetString() : null;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (!localMode && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidDataException($"OAuth metadata 缺少安全的 {name}");
        return await McpEndpointPolicy.ValidateAsync(uri.ToString(), localMode, cancellationToken);
    }
    private static bool AudienceMatches(JsonElement audience, string resource) => audience.ValueKind switch
    {
        JsonValueKind.String => string.Equals(audience.GetString()?.TrimEnd('/'), resource, StringComparison.Ordinal),
        JsonValueKind.Array => audience.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String &&
            string.Equals(x.GetString()?.TrimEnd('/'), resource, StringComparison.Ordinal)),
        _ => false
    };
    private static int ReserveLoopbackPort() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
    private static string CanonicalResource(Uri resource) => resource.GetLeftPart(UriPartial.Path).TrimEnd('/');
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static Uri BuildUri(Uri endpoint, IReadOnlyDictionary<string, string> values) { var query = string.Join('&', values.Where(x => x.Value.Length > 0).Select(x => Uri.EscapeDataString(x.Key) + "=" + Uri.EscapeDataString(x.Value))); return new(endpoint + (string.IsNullOrEmpty(endpoint.Query) ? "?" : "&") + query); }
    public void Dispose() => _http.Dispose();
}
