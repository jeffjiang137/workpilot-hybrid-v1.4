using WorkPilot.Contracts.Primitives;

namespace WorkPilot.Domain.Automation.Run;

/// <summary>
/// Per-node retry policy (doc 04 §10, RUN-004). Bounds are enforced by <see cref="Limits.V1_5"/> so no
/// literal numbers appear here. Construct via <see cref="Create"/>.
/// </summary>
public sealed record RetryPolicy(int MaxAttempts, int BaseDelaySeconds, int MaxDelaySeconds)
{
    /// <summary>Conservative default: up to 3 attempts, 5s base, 60s ceiling.</summary>
    public static readonly RetryPolicy Default = new(3, 5, 60);

    public static Result<RetryPolicy> Create(int maxAttempts, int baseDelaySeconds, int maxDelaySeconds)
    {
        if (maxAttempts < 1 || maxAttempts > Limits.V1_5.MaxRetryMaxAttempts)
            return Result<RetryPolicy>.Fail(RunErrors.RetryPolicyInvalidError(
                $"maxAttempts must be 1..{Limits.V1_5.MaxRetryMaxAttempts}"));
        if (baseDelaySeconds < 1 || baseDelaySeconds > Limits.V1_5.MaxRetryBaseDelaySeconds)
            return Result<RetryPolicy>.Fail(RunErrors.RetryPolicyInvalidError(
                $"baseDelaySeconds must be 1..{Limits.V1_5.MaxRetryBaseDelaySeconds}"));
        if (maxDelaySeconds < 1 || maxDelaySeconds > Limits.V1_5.MaxRetryMaxDelaySeconds)
            return Result<RetryPolicy>.Fail(RunErrors.RetryPolicyInvalidError(
                $"maxDelaySeconds must be 1..{Limits.V1_5.MaxRetryMaxDelaySeconds}"));
        if (maxDelaySeconds < baseDelaySeconds)
            return Result<RetryPolicy>.Fail(RunErrors.RetryPolicyInvalidError(
                "maxDelaySeconds must be >= baseDelaySeconds"));
        return Result<RetryPolicy>.Ok(new RetryPolicy(maxAttempts, baseDelaySeconds, maxDelaySeconds));
    }
}
