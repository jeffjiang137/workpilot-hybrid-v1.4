using WorkPilot.App.Core.Runs;
using WorkPilot.App.Core.Tests.Fakes;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation.Run;
using Xunit;

namespace WorkPilot.App.Core.Tests.Runs;

public class RunNotificationProjectorTests
{
    [Fact]
    public void Completed_maps_title_and_null_reason_not_security()
    {
        var ids = new SeqIdGenerator();
        var n = RunNotificationProjector.Project(RunTestFactory.MakeRun(ids, RunStatus.Completed, RunTestFactory.T0));
        Assert.Equal("RunNotification.Completed", n.TitleMessageKey);
        Assert.Null(n.ReasonCode);
        Assert.False(n.IsSecurityBlocked);
    }

    [Fact]
    public void Failed_carries_reason_code_and_is_not_security_blocked()
    {
        var ids = new SeqIdGenerator();
        var n = RunNotificationProjector.Project(RunTestFactory.MakeRun(ids, RunStatus.Failed, RunTestFactory.T0));
        Assert.Equal("RunNotification.Failed", n.TitleMessageKey);
        Assert.Equal("E_FAIL", n.ReasonCode);
        Assert.False(n.IsSecurityBlocked);
    }

    [Fact]
    public void BlockedPolicy_and_NeedsReview_are_security_blocked()
    {
        var ids = new SeqIdGenerator();
        var blocked = RunNotificationProjector.Project(RunTestFactory.MakeRun(ids, RunStatus.BlockedPolicy, RunTestFactory.T0));
        Assert.True(blocked.IsSecurityBlocked);
        Assert.Equal("RunNotification.BlockedPolicy", blocked.TitleMessageKey);
        Assert.Equal("E_BLOCK", blocked.ReasonCode);

        var review = RunNotificationProjector.Project(RunTestFactory.MakeRun(ids, RunStatus.NeedsReview, RunTestFactory.T0));
        Assert.True(review.IsSecurityBlocked);
        Assert.Equal("RunNotification.NeedsReview", review.TitleMessageKey);
    }

    [Fact]
    public void WaitingApproval_has_null_reason()
    {
        var ids = new SeqIdGenerator();
        var n = RunNotificationProjector.Project(RunTestFactory.MakeRun(ids, RunStatus.WaitingApproval, RunTestFactory.T0));
        Assert.Equal("RunNotification.WaitingApproval", n.TitleMessageKey);
        Assert.Null(n.ReasonCode);
        Assert.False(n.IsSecurityBlocked);
    }
}
