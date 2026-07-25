using System.Net;
using System.Net.Sockets;

namespace WorkPilot.Services;

public static class McpEndpointPolicy
{
    public static async Task<Uri> ValidateAsync(string endpoint, bool localMode, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment) || uri.Query.Contains("token", StringComparison.OrdinalIgnoreCase) ||
            uri.Query.Contains("key", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("MCP Endpoint URL 无效或包含疑似秘密");
        if (localMode)
        {
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("本地 MCP 仅支持 HTTP/HTTPS");
        }
        else if (uri.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException("远程 MCP 必须使用 HTTPS");
        var addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken);
        if (addresses.Length == 0) throw new InvalidOperationException("MCP 主机无法解析");
        foreach (var address in addresses)
        {
            var blocked = IsPrivateOrReserved(address);
            if (localMode && !IPAddress.IsLoopback(address)) throw new InvalidOperationException("本地模式只允许 loopback 地址");
            if (!localMode && blocked) throw new InvalidOperationException("远程 MCP 地址解析到私网或保留地址，已阻止");
        }
        return uri;
    }

    public static bool IsPrivateOrReserved(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ||
            address.Equals(IPAddress.None) || address.IsIPv6Multicast || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal)
            return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes(); return (bytes[0] & 0xFE) == 0xFC;
        }
        var b = address.GetAddressBytes();
        return b[0] is 0 or 10 or 127 || (b[0] == 100 && b[1] is >= 64 and <= 127) ||
            (b[0] == 169 && b[1] == 254) || (b[0] == 172 && b[1] is >= 16 and <= 31) ||
            (b[0] == 192 && b[1] == 0) || (b[0] == 192 && b[1] == 168) ||
            (b[0] == 192 && b[1] == 0 && b[2] == 2) ||
            (b[0] == 198 && b[1] is 18 or 19) || (b[0] == 198 && b[1] == 51 && b[2] == 100) ||
            (b[0] == 203 && b[1] == 0 && b[2] == 113) || b[0] >= 224;
    }
}
