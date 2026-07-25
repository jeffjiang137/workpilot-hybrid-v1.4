using System;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Domain.Automation;
using WorkPilot.Domain.Automation.Scheduling;
using Xunit;

namespace WorkPilot.Domain.Tests.Scheduling;

public sealed class MissedRunResolverTests
{
    private static readonly DateTimeOffset Anchor = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static ITimeZoneResolver Resolver => new AnyZoneResolver(ZoneFactory.EuLike());

    // SCH-A08: missed-run policies produce the right set of runs.
    [Fact]
    public void Skip_produces_no_runs_but_counts_them()
    {
        var trigger = Triggers.Interval(3600, Anchor);
        var last = Anchor;
        var now = Anchor.AddHours(10);
        var r = MissedRunResolver.Resolve(trigger, last, now, MissedRunPolicy.Skip, Resolver);
        Assert.Empty(r.Occurrences);
        Assert.Equal(10, r.SkippedCount);
    }

    [Fact]
    public void RunOnce_creates_only_the_most_recent_missed()
    {
        var trigger = Triggers.Interval(3600, Anchor);
        var r = MissedRunResolver.Resolve(trigger, Anchor, Anchor.AddHours(10), MissedRunPolicy.RunOnce, Resolver);
        Assert.Single(r.Occurrences);
        Assert.Equal(Anchor.AddHours(10), r.Occurrences[0]);
        Assert.Equal(9, r.SkippedCount);
    }

    // SCH-A09: catch_up caps at 5; the rest are summarized as skipped.
    [Fact]
    public void CatchUp_caps_at_five_and_summarizes_rest()
    {
        var trigger = Triggers.Interval(3600, Anchor);
        var r = MissedRunResolver.Resolve(trigger, Anchor, Anchor.AddHours(10), MissedRunPolicy.CatchUp, Resolver);
        Assert.Equal(Limits.V1_5.MaxCatchUpRuns, r.Occurrences.Count); // 5
        Assert.Equal(Anchor.AddHours(1), r.Occurrences[0]);
        Assert.Equal(Anchor.AddHours(5), r.Occurrences[4]);
        Assert.Equal(5, r.SkippedCount); // 10 total - 5 created
    }

    [Fact]
    public void No_missed_window_yields_nothing()
    {
        var trigger = Triggers.Interval(3600, Anchor);
        var r = MissedRunResolver.Resolve(trigger, Anchor, Anchor.AddMinutes(30), MissedRunPolicy.CatchUp, Resolver);
        Assert.Empty(r.Occurrences);
        Assert.Equal(0, r.SkippedCount);
    }
}
