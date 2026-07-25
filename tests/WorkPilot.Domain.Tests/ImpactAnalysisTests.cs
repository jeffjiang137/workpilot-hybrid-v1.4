using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation;
using Xunit;

namespace WorkPilot.Domain.Tests;

public class ImpactAnalysisTests
{
    private static readonly AutomationId Auto = AutomationId.Parse("auto_1");

    [Fact]
    public void Identical_revisions_have_no_diff()
    {
        var (a, _) = Samples.MakeRevision(Auto, 1);
        var (b, _) = Samples.MakeRevision(Auto, 1);
        var diff = RevisionDiff.Compute(a, b);
        Assert.False(diff.HasChanges);
        Assert.Empty(diff.Changes);
    }

    [Fact]
    public void Budget_change_is_reported_as_a_field_change()
    {
        var (a, _) = Samples.MakeRevision(Auto, 1, Samples.Budget(maxTokens: 100_000));
        var (b, _) = Samples.MakeRevision(Auto, 2, Samples.Budget(maxTokens: 200_000));
        var diff = RevisionDiff.Compute(a, b);
        Assert.True(diff.HasChanges);
        var change = Assert.Single(diff.Changes);
        Assert.Equal("budget.max_total_tokens", change.Path);
        Assert.Equal("100000", change.From);
        Assert.Equal("200000", change.To);
    }

    [Fact]
    public void Permission_scope_change_is_reported()
    {
        var (a, _) = Samples.MakeRevision(Auto, 1, scope: "read-only");
        var (b, _) = Samples.MakeRevision(Auto, 2, scope: "read-write");
        var diff = RevisionDiff.Compute(a, b);
        var change = Assert.Single(diff.Changes);
        Assert.Equal("permission_request.scope", change.Path);
        Assert.Equal("read-only", change.From);
        Assert.Equal("read-write", change.To);
    }
}
