using System.Collections.Concurrent;
using System.Net;
using WorkPilot.Models;

namespace WorkPilot.Services;

public sealed class ConnectorExecutionGate
{
    private readonly SemaphoreSlim _global = new(12, 12);
    private readonly ConcurrentDictionary<string, AccountGate> _accounts = new();

    public async Task<CapabilityResult> ExecuteAsync(string accountId, bool mutating,
        Func<CancellationToken, Task<CapabilityResult>> operation, CancellationToken cancellationToken)
    {
        var account = _accounts.GetOrAdd(accountId, _ => new AccountGate());
        if (Interlocked.Increment(ref account.Queued) > 100)
        {
            Interlocked.Decrement(ref account.Queued); throw new InvalidOperationException("连接器等待队列已满（100）");
        }
        try
        {
            await _global.WaitAsync(cancellationToken); await account.Concurrent.WaitAsync(cancellationToken);
            try
            {
                CheckCircuit(account); await WaitForTokenAsync(account, cancellationToken);
                var delays = new[] { 250, 1000, 3000 };
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        var result = await operation(cancellationToken); RecordSuccess(account); return result;
                    }
                    catch (Exception error) when (!mutating && attempt < delays.Length && IsRetryable(error))
                    {
                        RecordFailure(account); await Task.Delay(delays[attempt], cancellationToken);
                    }
                    catch { RecordFailure(account); throw; }
                }
            }
            finally { account.Concurrent.Release(); _global.Release(); }
        }
        finally { Interlocked.Decrement(ref account.Queued); }
    }

    private static async Task WaitForTokenAsync(AccountGate account, CancellationToken cancellationToken)
    {
        TimeSpan delay;
        lock (account.Gate)
        {
            var now = DateTimeOffset.UtcNow; var elapsed = (now - account.LastRefill).TotalSeconds;
            account.Tokens = Math.Min(10, account.Tokens + elapsed * 2); account.LastRefill = now;
            if (account.Tokens >= 1) { account.Tokens--; return; }
            delay = TimeSpan.FromSeconds((1 - account.Tokens) / 2); account.Tokens = 0;
        }
        await Task.Delay(delay, cancellationToken);
    }

    private static void CheckCircuit(AccountGate account)
    {
        lock (account.Gate)
        {
            if (account.OpenUntil > DateTimeOffset.UtcNow) throw new InvalidOperationException("连接器熔断器已开启，请稍后重试");
            if (account.OpenUntil != default) { account.OpenUntil = default; account.Failures.Clear(); }
        }
    }

    private static void RecordSuccess(AccountGate account)
    {
        lock (account.Gate) { account.Failures.Clear(); account.OpenUntil = default; }
    }

    private static void RecordFailure(AccountGate account)
    {
        lock (account.Gate)
        {
            var now = DateTimeOffset.UtcNow; account.Failures.Enqueue(now);
            while (account.Failures.TryPeek(out var first) && now - first > TimeSpan.FromSeconds(60)) account.Failures.Dequeue();
            if (account.Failures.Count >= 5) account.OpenUntil = now.AddSeconds(30);
        }
    }

    private static bool IsRetryable(Exception error) => error is HttpRequestException or TaskCanceledException ||
        error is ConnectorHttpException connector && connector.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private sealed class AccountGate
    {
        public readonly object Gate = new(); public readonly SemaphoreSlim Concurrent = new(4, 4);
        public readonly Queue<DateTimeOffset> Failures = new(); public double Tokens = 10;
        public DateTimeOffset LastRefill = DateTimeOffset.UtcNow; public DateTimeOffset OpenUntil; public int Queued;
    }
}
