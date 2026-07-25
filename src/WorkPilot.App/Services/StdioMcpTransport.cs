using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace WorkPilot.Services;

public sealed class StdioMcpTransport(
    string executable, IReadOnlyList<string> arguments, string? workingDirectory) : IMcpTransport
{
    private const int MaxMessageBytes = 4 * 1024 * 1024;
    private static readonly HashSet<string> BlockedHosts = new(StringComparer.OrdinalIgnoreCase)
        { "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "mshta.exe" };
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private Process? _process; private WindowsJob? _job; private Task? _readerTask; private Task? _stderrTask;
    private readonly Queue<string> _stderr = new(); private int _stderrBytes; private int _invalidLines;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_process is not null) return Task.CompletedTask;
        var fullPath = Path.GetFullPath(executable);
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(fullPath) ||
            !string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase) ||
            BlockedHosts.Contains(Path.GetFileName(fullPath)) || fullPath.StartsWith("\\\\", StringComparison.Ordinal))
            throw new InvalidOperationException("MCP executable 必须是可信的本地绝对 .exe，且不能使用 shell 宿主");
        if (arguments.Count > 64 || arguments.Sum(x => x.Length) > 32_000 || arguments.Any(x => x.Contains('\0')))
            throw new ArgumentException("MCP 参数数量或长度超过上限");
        var cwd = string.IsNullOrWhiteSpace(workingDirectory) ? Path.GetDirectoryName(fullPath)! : Path.GetFullPath(workingDirectory);
        if (!Directory.Exists(cwd) || cwd.StartsWith("\\\\", StringComparison.Ordinal)) throw new InvalidOperationException("MCP 工作目录无效");
        var start = new ProcessStartInfo
        {
            FileName = fullPath, WorkingDirectory = cwd, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        _process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 MCP 服务");
        try { _job = WindowsJob.Attach(_process); }
        catch { if (!_process.HasExited) _process.Kill(true); _process.Dispose(); _process = null; throw; }
        _readerTask = ReadLoopAsync(_shutdown.Token); _stderrTask = ReadStderrAsync(_shutdown.Token);
        return Task.CompletedTask;
    }

    public async Task<JsonElement> RequestAsync(long id, string method, object? parameters,
        CancellationToken cancellationToken)
    {
        if (_process is null || _process.HasExited) throw new McpProtocolException("MCP stdio 服务未运行");
        if (_pending.Count >= 64) throw new InvalidOperationException("MCP pending request 已达 64 上限");
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion)) throw new InvalidOperationException("MCP request ID 冲突");
        try
        {
            await WriteAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = id.ToString(), method, @params = parameters }), cancellationToken);
            using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(method == "initialize" ? 10 : 60), cancellationToken);
        }
        finally { _pending.TryRemove(id, out _); }
    }

    public Task NotifyAsync(string method, object? parameters, CancellationToken cancellationToken) =>
        WriteAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params = parameters }), cancellationToken);

    private async Task WriteAsync(string json, CancellationToken cancellationToken)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxMessageBytes) throw new InvalidOperationException("MCP 消息超过 4 MiB");
        await _writeGate.WaitAsync(cancellationToken);
        try { await _process!.StandardInput.WriteLineAsync(json.AsMemory(), cancellationToken); await _process.StandardInput.FlushAsync(cancellationToken); }
        finally { _writeGate.Release(); }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _process is { HasExited: false })
            {
                var line = await _process.StandardOutput.ReadLineAsync(cancellationToken); if (line is null) break;
                if (Encoding.UTF8.GetByteCount(line) > MaxMessageBytes) throw new McpProtocolException("MCP stdout 消息超过 4 MiB");
                try
                {
                    using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 64 });
                    var root = document.RootElement;
                    if (!root.TryGetProperty("id", out var idValue)) continue;
                    if (!long.TryParse(idValue.ToString(), out var id) || !_pending.TryGetValue(id, out var completion)) continue;
                    if (root.TryGetProperty("error", out var error)) completion.TrySetException(new McpProtocolException("MCP 错误：" + Limit(error.ToString(), 800)));
                    else if (root.TryGetProperty("result", out var result)) completion.TrySetResult(result.Clone());
                    else completion.TrySetException(new McpProtocolException("MCP response 缺少 result/error"));
                    _invalidLines = 0;
                }
                catch (JsonException error)
                {
                    if (++_invalidLines >= 3) throw new McpProtocolException("MCP stdout 连续包含非协议内容", error);
                }
            }
            if (!_shutdown.IsCancellationRequested) FailPending(new McpProtocolException("MCP stdio 服务已退出"));
        }
        catch (Exception error) { FailPending(error); }
    }

    private async Task ReadStderrAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _process is { HasExited: false })
            {
                var line = await _process.StandardError.ReadLineAsync(cancellationToken); if (line is null) break;
                line = Limit(line.ReplaceLineEndings(" "), 4096); _stderr.Enqueue(line); _stderrBytes += Encoding.UTF8.GetByteCount(line);
                while (_stderrBytes > 1024 * 1024 && _stderr.TryDequeue(out var removed)) _stderrBytes -= Encoding.UTF8.GetByteCount(removed);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void FailPending(Exception error)
    {
        foreach (var completion in _pending.Values) completion.TrySetException(error);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _shutdown.Cancel(); if (_process is null) return;
        try { _process.StandardInput.Close(); if (!_process.HasExited) await _process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(2), cancellationToken); }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException or InvalidOperationException)
            { if (!_process.HasExited) _process.Kill(true); }
        if (_readerTask is not null) await IgnoreReaderShutdownAsync(_readerTask);
        if (_stderrTask is not null) await IgnoreReaderShutdownAsync(_stderrTask);
        _process.Dispose(); _process = null; _job?.Dispose(); _job = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None); _shutdown.Dispose(); _writeGate.Dispose();
    }

    private static async Task IgnoreReaderShutdownAsync(Task task)
    {
        try { await task; }
        catch (Exception error) when (error is OperationCanceledException or McpProtocolException or IOException)
        { AppLogger.Error("MCP reader stopped during shutdown", error); }
    }
    private static string Limit(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}
