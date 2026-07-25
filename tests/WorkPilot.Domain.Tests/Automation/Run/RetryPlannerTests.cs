using System;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Domain.Automation.Run;
using Xunit;

namespace WorkPilot.Domain.Tests.Automation.Run;

public class RetryPlannerTests
{
    private static readonly RetryPolicy Policy = new(3, 5, 60);
    private static readonly IRandomSource Rand = new DeterministicRandom(12345);

    [Fact]
    public void Cap_grows_exponentially_and_is_capped()
    {
        // cap = min(maxDelay, base * 2^(attempt-1))
        Assert.Equal(5, CapFor(1));
        Assert.Equal(10, CapFor(2));
        Assert.Equal(20, CapFor(3));
        Assert.Equal(40, CapFor(4));
        Assert.Equal(60, CapFor(6)); // capped at maxDelay 60
    }

    private static double CapFor(int attempt)
    {
        var exp = Math.Pow(2, attempt - 1);
        return Math.Min(Policy.MaxDelaySeconds, Policy.BaseDelaySeconds * exp);
    }

    [Fact]
    public void Delay_is_within_zero_to_cap()
    {
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            var delay = RetryPlanner.ComputeDelay(Policy, attempt, Rand);
            Assert.False(delay.Defer);
            Assert.True(delay.WaitSeconds >= 0, $"attempt {attempt} lower bound");
            Assert.True(delay.WaitSeconds < CapFor(attempt) + 1e-9, $"attempt {attempt} upper bound");
        }
    }

    [Fact]
    public void Same_seed_is_deterministic()
    {
        var a = RetryPlanner.ComputeDelay(Policy, 3, new DeterministicRandom(999));
        var b = RetryPlanner.ComputeDelay(Policy, 3, new DeterministicRandom(999));
        Assert.Equal(a.WaitSeconds, b.WaitSeconds, 6);
    }

    [Fact]
    public void Server_retry_after_is_honored_when_larger()
    {
        // jitter in [0,5); server 300s -> chosen=300 (<=900) not deferred.
        var d = RetryPlanner.ComputeDelay(Policy, 1, Rand, TimeSpan.FromSeconds(300));
        Assert.False(d.Defer);
        Assert.Equal(300, d.WaitSeconds);
    }

    [Fact]
    public void Exceeding_fifteen_minutes_defers()
    {
        var d = RetryPlanner.ComputeDelay(Policy, 1, Rand, TimeSpan.FromSeconds(1000));
        Assert.True(d.Defer);
        Assert.Equal(0, d.WaitSeconds);
    }

    [Fact]
    public void Negative_attempt_is_treated_as_first()
    {
        var d = RetryPlanner.ComputeDelay(Policy, -3, Rand);
        Assert.False(d.Defer);
        Assert.True(d.WaitSeconds >= 0 && d.WaitSeconds < 5 + 1e-9);
    }
}
