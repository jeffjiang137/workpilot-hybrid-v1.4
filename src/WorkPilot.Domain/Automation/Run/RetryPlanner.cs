using System;
using WorkPilot.Contracts.Primitives;

namespace WorkPilot.Domain.Automation.Run;

/// <summary>Result of <see cref="RetryPlanner.ComputeDelay"/> (doc 04 §10).</summary>
public sealed record RetryDelay(double WaitSeconds, bool Defer)
{
    /// <summary>Server Retry-After / jitter exceeded the 15-minute ceiling: defer, do not retry inline.</summary>
    public static readonly RetryDelay Deferred = new(0, true);
}

/// <summary>
/// Full-jitter backoff (doc 04 §10). Pure: <c>cap = min(maxDelay, base * 2^(attempt-1))</c>,
/// <c>delay = Uniform(0, cap)</c> via the injected <see cref="IRandomSource"/>. The server's
/// legitimate <c>Retry-After</c> is honored as <c>max(jitter, retryAfter)</c> but anything above
/// <see cref="Limits.V1_5.MaxRetryWaitSeconds"/> defers.
/// </summary>
public static class RetryPlanner
{
    public static RetryDelay ComputeDelay(
        RetryPolicy policy,
        int attempt,
        IRandomSource random,
        TimeSpan? serverRetryAfter = null)
    {
        if (policy is null) throw new ArgumentNullException(nameof(policy));
        if (random is null) throw new ArgumentNullException(nameof(random));
        if (attempt < 1) attempt = 1;

        var exp = Math.Pow(2, attempt - 1);
        var cap = Math.Min(policy.MaxDelaySeconds, policy.BaseDelaySeconds * exp);
        var jitter = random.NextDouble() * cap; // [0, cap)

        var chosen = jitter;
        if (serverRetryAfter.HasValue)
            chosen = Math.Max(chosen, serverRetryAfter.Value.TotalSeconds);

        if (chosen > Limits.V1_5.MaxRetryWaitSeconds)
            return RetryDelay.Deferred;

        return new RetryDelay(chosen, false);
    }
}
