using System;
using WorkPilot.Domain.Automation;

namespace WorkPilot.Domain.Tests.Scheduling;

/// <summary>Convenience builders for <see cref="TriggerDefinition"/> in tests.</summary>
internal static class Triggers
{
    public static TriggerDefinition Interval(long intervalSeconds, DateTimeOffset anchor) => new(
        "interval_1", TriggerType.Interval, true, null, null, null, intervalSeconds, anchor,
        null, null, null, null, null, null);

    public static TriggerDefinition CalendarDaily(string localTime, int[] days, string tz = "TestZone",
        DateTimeOffset? start = null, DateTimeOffset? end = null) => new(
        "cal_1", TriggerType.CalendarDaily, true, tz, start, end, null, null, localTime, days,
        null, null, null, null);

    public static TriggerDefinition CalendarMonthly(string localTime, int? dayOfMonth, string? missingDay,
        string tz = "TestZone", DateTimeOffset? start = null, DateTimeOffset? end = null) => new(
        "cal_1", TriggerType.CalendarMonthly, true, tz, start, end, null, null, localTime, null,
        dayOfMonth, missingDay, null, null);

    public static TriggerDefinition Once(DateTimeOffset start, string tz = "TestZone") => new(
        "once_1", TriggerType.Once, true, tz, start, null, null, null, "09:00", null,
        null, null, null, null);
}
