using WorkPilot.Domain.Automation.Run;
using WorkPilot.Domain.Automation.Run.Materialization;
using WorkPilot.Domain.Automation.Scheduling;
using Xunit;

namespace WorkPilot.Domain.Tests;

/// <summary>RUN-002/009: the status→category mapping the claim/concurrency logic reasons about.</summary>
public class RunStatusCategoryExtensionsTests
{
    [Theory]
    [InlineData(RunStatus.Queued, RunStatusCategory.Queued)]
    [InlineData(RunStatus.Claimed, RunStatusCategory.Active)]
    [InlineData(RunStatus.Running, RunStatusCategory.Active)]
    [InlineData(RunStatus.WaitingDelay, RunStatusCategory.Active)]
    [InlineData(RunStatus.WaitingApproval, RunStatusCategory.Active)]
    [InlineData(RunStatus.Completed, RunStatusCategory.Terminal)]
    [InlineData(RunStatus.Failed, RunStatusCategory.Terminal)]
    [InlineData(RunStatus.Cancelled, RunStatusCategory.Terminal)]
    [InlineData(RunStatus.NeedsReview, RunStatusCategory.Terminal)]
    [InlineData(RunStatus.BlockedPolicy, RunStatusCategory.Terminal)]
    public void ToCategory_maps_correctly(RunStatus status, RunStatusCategory expected)
        => Assert.Equal(expected, status.ToCategory());
}
