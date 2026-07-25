using System.Linq;
using WorkPilot.App.Core.Runs;
using Xunit;

namespace WorkPilot.App.Core.Tests.Runs;

public class SafeSummaryProjectorTests
{
    [Fact]
    public void Target_fields_are_hashed_others_by_size_only()
    {
        var s = SafeSummaryProjector.Project(
            "{\"name\":\"hello\",\"webhook\":\"https://x\"}",
            "{\"email\":\"a@b.com\"}");

        var name = s.Inputs.Single(f => f.Name == "name");
        Assert.False(name.IsTarget);
        Assert.Null(name.TargetAlias);
        Assert.True(name.ByteSize > 0);

        var wh = s.Inputs.Single(f => f.Name == "webhook");
        Assert.True(wh.IsTarget);
        Assert.NotNull(wh.TargetAlias);
        Assert.Equal(16, wh.TargetAlias!.Length);                 // truncated SHA-256 alias
        Assert.DoesNotContain("https://x", wh.TargetAlias);       // original value never retained

        var em = s.Outputs.Single(f => f.Name == "email");
        Assert.True(em.IsTarget);
        Assert.True(s.HasTarget);
    }

    [Fact]
    public void Empty_and_invalid_json_yield_empty_summary_without_throwing()
    {
        Assert.Empty(SafeSummaryProjector.Project(null, null).Inputs);
        Assert.Empty(SafeSummaryProjector.Project(null, null).Outputs);

        Assert.Empty(SafeSummaryProjector.Project("not json", "[]").Inputs);
        Assert.Empty(SafeSummaryProjector.Project("[1,2,3]", "{}").Inputs); // array is not an object
    }

    [Fact]
    public void Byte_counts_accumulate_across_fields()
    {
        var s = SafeSummaryProjector.Project("{\"a\":\"xy\",\"b\":\"z\"}", null);
        Assert.Equal(2 + 1, s.InputBytes);
        Assert.Equal(2, s.InputCount);
    }
}
