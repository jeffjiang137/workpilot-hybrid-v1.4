using System.Text.Json;

namespace WorkPilot.Services;

public interface IMcpTransport : IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken);
    Task<JsonElement> RequestAsync(long id, string method, object? parameters, CancellationToken cancellationToken);
    Task NotifyAsync(string method, object? parameters, CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

public sealed class McpProtocolException : Exception
{
    public McpProtocolException(string message) : base(message) { }
    public McpProtocolException(string message, Exception innerException) : base(message, innerException) { }
}
