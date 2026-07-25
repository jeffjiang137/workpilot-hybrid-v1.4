using WorkPilot.Contracts.Primitives;
using WorkPilot.Domain.Automation.Run;
using Xunit;

namespace WorkPilot.Domain.Tests.Automation.Run;

public class RetryPolicyTests
{
    [Fact]
    public void Default_is_three_attempts()
        => Assert.Equal(3, RetryPolicy.Default.MaxAttempts);

    [Fact]
    public void Create_accepts_in_range_policy()
    {
        var r = RetryPolicy.Create(3, 5, 60);
        Assert.True(r.IsSuccess);
        Assert.Equal(3, r.Value!.MaxAttempts);
    }

    [Theory]
    [InlineData(0, 5, 60)]   // maxAttempts too low
    [InlineData(4, 5, 60)]   // maxAttempts too high (>3)
    [InlineData(3, 0, 60)]   // base too low
    [InlineData(3, 61, 60)]  // base too high (>60)
    [InlineData(3, 5, 0)]    // max too low
    [InlineData(3, 5, 301)]  // max too high (>300)
    [InlineData(3, 60, 30)]  // max < base
    public void Create_rejects_out_of_range(int max, int @base, int maxDelay)
        => Assert.True(RetryPolicy.Create(max, @base, maxDelay).IsSuccess == false);

    [Fact]
    public void Create_bounds_match_limits()
    {
        Assert.True(RetryPolicy.Create(Limits.V1_5.MaxRetryMaxAttempts, 1, 1).IsSuccess);
        Assert.True(RetryPolicy.Create(1, Limits.V1_5.MaxRetryBaseDelaySeconds, Limits.V1_5.MaxRetryBaseDelaySeconds).IsSuccess);
        Assert.True(RetryPolicy.Create(1, 1, Limits.V1_5.MaxRetryMaxDelaySeconds).IsSuccess);
    }
}
