using System;
using System.Collections.Generic;
using System.Linq;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Domain.Automation;
using WorkPilot.Domain.Automation.Scheduling;
using WorkPilot.Domain.Automation.Validation;
using Xunit;

namespace WorkPilot.Domain.Tests.Scheduling;

/// <summary>
/// Property-style invariant checks over many generated inputs (spec doc 12: "property tests" for
/// Schedule/DST/Workflow). No external framework — a seeded <see cref="Random"/> drives the loop, so
/// failures are reproducible.
/// </summary>
public sealed class SchedulePropertyTests
{
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static ITimeZoneResolver Resolver => new AnyZoneResolver(ZoneFactory.EuLike());

    [Fact]
    public void Interval_next_is_strictly_increasing_and_drift_free_across_many_inputs()
    {
        var rand = new Random(20260721);
        for (var i = 0; i < 500; i++)
        {
            var interval = rand.Next(Limits.V1_5.MinIntervalSeconds, Limits.V1_5.MaxIntervalSeconds);
            var anchor = Base.AddSeconds(rand.Next(0, 20_000_000));
            var trigger = Triggers.Interval(interval, anchor);

            var after = anchor.AddSeconds(rand.Next(0, 50_000_000));
            var r = ScheduleCalculator.ComputeNext(trigger, after, Resolver);
            Assert.True(r.HasOccurrence);
            var occ = r.Occurrence!.Utc;

            // strictly after the query point
            Assert.True(occ > after);
            // equals anchor + n*interval exactly (no floating drift)
            var deltaTicks = occ.UtcTicks - anchor.UtcTicks;
            Assert.Equal(0, deltaTicks % (interval * TimeSpan.TicksPerSecond));
            // monotonic: next after this occurrence is even later
            var r2 = ScheduleCalculator.ComputeNext(trigger, occ, Resolver);
            Assert.True(r2.Occurrence!.Utc > occ);
        }
    }

    [Fact]
    public void Calendar_next_after_occurrence_is_always_later_than_the_occurrence()
    {
        var rand = new Random(99);
        for (var i = 0; i < 200; i++)
        {
            var day = rand.Next(0, 6);
            var trigger = Triggers.CalendarDaily("09:00", new[] { day }, "TestZone");
            var after = Base.AddDays(rand.Next(0, 400));
            var r = ScheduleCalculator.ComputeNext(trigger, after, Resolver);
            if (!r.HasOccurrence) continue;
            var occ = r.Occurrence!.Utc;
            var r2 = ScheduleCalculator.ComputeNext(trigger, occ, Resolver);
            Assert.True(r2.Occurrence!.Utc > occ);
        }
    }
}
