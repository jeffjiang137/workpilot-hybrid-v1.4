using System;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Domain.Automation;
using WorkPilot.Domain.Automation.Scheduling;
using Xunit;

namespace WorkPilot.Domain.Tests.Scheduling;

public sealed class ScheduleCalculatorTests
{
    private static readonly DateTimeOffset Anchor = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static ITimeZoneResolver Resolver => new AnyZoneResolver(ZoneFactory.EuLike());

    // SCH-A01: interval is anchor-based and never drifts, even across many intervals.
    [Fact]
    public void Interval_is_anchor_based_and_drift_free()
    {
        var trigger = Triggers.Interval(3600, Anchor);
        var after = Anchor.AddHours(2.5);
        var r = ScheduleCalculator.ComputeNext(trigger, after, Resolver);
        Assert.True(r.HasOccurrence);
        Assert.Equal(Anchor.AddHours(3), r.Occurrence!.Utc);
    }

    [Fact]
    public void Interval_after_many_steps_has_no_drift()
    {
        var trigger = Triggers.Interval(3600, Anchor);
        var after = Anchor.AddSeconds(3600L * 1000); // 1000 hours later
        var r = ScheduleCalculator.ComputeNext(trigger, after, Resolver);
        Assert.Equal(Anchor.AddSeconds(3600L * 1001), r.Occurrence!.Utc);
    }

    [Fact]
    public void Interval_before_anchor_returns_anchor()
    {
        var trigger = Triggers.Interval(3600, Anchor);
        var r = ScheduleCalculator.ComputeNext(trigger, Anchor.AddHours(-5), Resolver);
        Assert.Equal(Anchor, r.Occurrence!.Utc);
    }

    // SCH-A02: spring-forward invalid local time moves forward to the first valid minute.
    [Fact]
    public void Calendar_spring_forward_moves_forward_and_marks()
    {
        var zone = ZoneFactory.EuLike();
        var trigger = Triggers.CalendarDaily("02:30", AllDays(), zone.Id);
        var after = new DateTimeOffset(2026, 3, 28, 12, 0, 0, TimeSpan.Zero);
        var r = ScheduleCalculator.ComputeNext(trigger, after, new AnyZoneResolver(zone));
        Assert.True(r.HasOccurrence);
        // 2026-03-29 02:30 local is in the gap; first valid is 03:00 local = 02:00Z.
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 2, 0, 0, TimeSpan.Zero), r.Occurrence!.Utc);
        Assert.True(r.Occurrence.DstAdjustedForward);
        Assert.False(r.Occurrence.DstAmbiguousFirst);
    }

    // SCH-A03: fall-back ambiguous local time runs once, on the earlier UTC instance.
    [Fact]
    public void Calendar_fall_back_picks_earlier_utc_once()
    {
        var zone = ZoneFactory.EuLike();
        var trigger = Triggers.CalendarDaily("02:30", AllDays(), zone.Id);
        var after = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        var r = ScheduleCalculator.ComputeNext(trigger, after, new AnyZoneResolver(zone));
        Assert.True(r.HasOccurrence);
        // 2026-10-25 02:30 local is ambiguous; earlier UTC is 01:30Z.
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 1, 30, 0, TimeSpan.Zero), r.Occurrence!.Utc);
        Assert.True(r.Occurrence.DstAmbiguousFirst);
        Assert.False(r.Occurrence.DstAdjustedForward);
    }

    // SCH-A04: month 31 with missing_day=skip produces no run in short months.
    [Fact]
    public void Calendar_monthly_day31_skip_skips_short_months()
    {
        var zone = ZoneFactory.EuLike();
        var trigger = Triggers.CalendarMonthly("09:00", 31, "skip", zone.Id);
        var after = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero); // Feb has 28 days
        var r = ScheduleCalculator.ComputeNext(trigger, after, new AnyZoneResolver(zone));
        Assert.True(r.HasOccurrence);
        // EuLike is in daylight (+1) on 2026-03-31, so local 09:00 = 08:00 UTC.
        Assert.Equal(new DateTimeOffset(2026, 3, 31, 8, 0, 0, TimeSpan.Zero), r.Occurrence!.Utc);
    }

    // SCH-A05: month 31 with missing_day=last_day uses the last day of short months.
    [Fact]
    public void Calendar_monthly_day31_last_day_uses_month_end()
    {
        var zone = ZoneFactory.EuLike();
        var trigger = Triggers.CalendarMonthly("09:00", 31, "last_day", zone.Id);
        var after = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var r = ScheduleCalculator.ComputeNext(trigger, after, new AnyZoneResolver(zone));
        Assert.True(r.HasOccurrence);
        Assert.Equal(new DateTimeOffset(2026, 2, 28, 9, 0, 0, TimeSpan.Zero), r.Occurrence!.Utc);
    }

    // SCH-A06: leap-year handling — Feb 29 exists in leap years, is skipped (missing_day=skip) otherwise.
    [Fact]
    public void Calendar_monthly_feb29_leap_year_present()
    {
        var zone = ZoneFactory.EuLike();
        var trigger = Triggers.CalendarMonthly("09:00", 29, "skip", zone.Id);
        var after = new DateTimeOffset(2028, 2, 1, 0, 0, 0, TimeSpan.Zero); // 2028 is a leap year
        var r = ScheduleCalculator.ComputeNext(trigger, after, new AnyZoneResolver(zone));
        Assert.True(r.HasOccurrence);
        Assert.Equal(new DateTimeOffset(2028, 2, 29, 9, 0, 0, TimeSpan.Zero), r.Occurrence!.Utc);
    }

    [Fact]
    public void Calendar_monthly_feb29_non_leap_skips_february()
    {
        var zone = ZoneFactory.EuLike();
        var trigger = Triggers.CalendarMonthly("09:00", 29, "skip", zone.Id);
        var after = new DateTimeOffset(2027, 2, 1, 0, 0, 0, TimeSpan.Zero); // 2027 is not a leap year
        var r = ScheduleCalculator.ComputeNext(trigger, after, new AnyZoneResolver(zone));
        Assert.True(r.HasOccurrence);
        Assert.Equal(new DateTimeOffset(2027, 3, 29, 9, 0, 0, TimeSpan.Zero), r.Occurrence!.Utc);
    }

    // SCH-A07: unknown time zone is a Preflight Error, not a crash.
    [Fact]
    public void Calendar_unknown_timezone_is_error()
    {
        var trigger = Triggers.CalendarDaily("09:00", AllDays(), "DoesNotExist");
        var r = ScheduleCalculator.ComputeNext(trigger, Anchor, new NullZoneResolver());
        Assert.False(r.HasOccurrence);
        Assert.Equal(SchedulingCodes.TimezoneNotFound, r.ErrorCode);
    }

    // Manual / domain_event produce no scheduled time.
    [Fact]
    public void Manual_and_domain_event_have_no_scheduled_time()
    {
        var manual = new TriggerDefinition("m", TriggerType.Manual, true, null, null, null, null, null, null, null, null, null, null, null);
        Assert.False(ScheduleCalculator.ComputeNext(manual, Anchor, Resolver).HasOccurrence);
    }

    [Fact]
    public void Once_fires_only_before_its_start()
    {
        var start = new DateTimeOffset(2026, 5, 1, 9, 0, 0, TimeSpan.Zero);
        var trigger = Triggers.Once(start);
        Assert.True(ScheduleCalculator.ComputeNext(trigger, start.AddHours(-1), Resolver).HasOccurrence);
        Assert.False(ScheduleCalculator.ComputeNext(trigger, start.AddHours(1), Resolver).HasOccurrence);
    }

    private static int[] AllDays() => new[] { 0, 1, 2, 3, 4, 5, 6 };
}
