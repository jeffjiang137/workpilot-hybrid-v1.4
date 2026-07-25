using System;
using System.Text.Json.Nodes;
using WorkPilot.Domain.Automation;
using WorkPilot.Domain.Automation.Validation;
using Xunit;

namespace WorkPilot.Domain.Tests.Validation;

public sealed class TriggerValidatorTests
{
    private static DateTimeOffset Now => new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Interval_valid_passes()
    {
        var t = new TriggerDefinition("i", TriggerType.Interval, true, null, null, null, 3600, Now, null, null, null, null, null, null);
        Assert.True(TriggerValidator.Validate(t).IsValid);
    }

    [Fact]
    public void Interval_below_minimum_is_invalid()
    {
        var t = new TriggerDefinition("i", TriggerType.Interval, true, null, null, null, 30, Now, null, null, null, null, null, null);
        var r = TriggerValidator.Validate(t);
        Assert.Contains(r.Errors, e => e.Code == ValidationCodes.IntervalSecondsInvalid);
    }

    [Fact]
    public void Interval_without_anchor_is_invalid()
    {
        var t = new TriggerDefinition("i", TriggerType.Interval, true, null, null, null, 3600, null, null, null, null, null, null, null);
        var r = TriggerValidator.Validate(t);
        Assert.Contains(r.Errors, e => e.Code == ValidationCodes.IntervalAnchorMissing);
    }

    [Fact]
    public void Calendar_daily_valid_passes()
    {
        var t = new TriggerDefinition("c", TriggerType.CalendarDaily, true, "TestZone", null, null, null, null, "09:00",
            new[] { 1, 2, 3 }, null, null, null, null);
        Assert.True(TriggerValidator.Validate(t).IsValid);
    }

    [Fact]
    public void Calendar_invalid_local_time_is_detected()
    {
        var t = new TriggerDefinition("c", TriggerType.CalendarDaily, true, "TestZone", null, null, null, null, "25:00",
            new[] { 1 }, null, null, null, null);
        var r = TriggerValidator.Validate(t);
        Assert.Contains(r.Errors, e => e.Code == ValidationCodes.CalendarLocalTimeInvalid);
    }

    [Fact]
    public void Calendar_empty_days_of_week_is_invalid()
    {
        var t = new TriggerDefinition("c", TriggerType.CalendarDaily, true, "TestZone", null, null, null, null, "09:00",
            Array.Empty<int>(), null, null, null, null);
        var r = TriggerValidator.Validate(t);
        Assert.Contains(r.Errors, e => e.Code == ValidationCodes.CalendarDaysOfWeekInvalid);
    }

    [Fact]
    public void Monthly_valid_day_and_last_day_pass()
    {
        var t1 = new TriggerDefinition("m", TriggerType.CalendarMonthly, true, "TestZone", null, null, null, null, "09:00",
            null, 15, null, null, null);
        var t2 = new TriggerDefinition("m", TriggerType.CalendarMonthly, true, "TestZone", null, null, null, null, "09:00",
            null, 31, "last_day", null, null);
        Assert.True(TriggerValidator.Validate(t1).IsValid);
        Assert.True(TriggerValidator.Validate(t2).IsValid);
    }

    [Fact]
    public void Monthly_invalid_day_is_detected()
    {
        var t = new TriggerDefinition("m", TriggerType.CalendarMonthly, true, "TestZone", null, null, null, null, "09:00",
            null, 32, "skip", null, null);
        var r = TriggerValidator.Validate(t);
        Assert.Contains(r.Errors, e => e.Code == ValidationCodes.MonthlyDayInvalid);
    }

    [Fact]
    public void Monthly_invalid_missing_day_is_detected()
    {
        var t = new TriggerDefinition("m", TriggerType.CalendarMonthly, true, "TestZone", null, null, null, null, "09:00",
            null, 31, "bogus", null, null);
        var r = TriggerValidator.Validate(t);
        Assert.Contains(r.Errors, e => e.Code == ValidationCodes.MonthlyMissingDayInvalid);
    }

    [Fact]
    public void Once_valid_passes_and_missing_start_fails()
    {
        var ok = new TriggerDefinition("o", TriggerType.Once, true, "TestZone", Now, null, null, null, "09:00", null, null, null, null, null);
        Assert.True(TriggerValidator.Validate(ok).IsValid);
        var bad = new TriggerDefinition("o", TriggerType.Once, true, "TestZone", null, null, null, null, "09:00", null, null, null, null, null);
        Assert.Contains(TriggerValidator.Validate(bad).Errors, e => e.Code == ValidationCodes.OnceFieldsMissing);
    }

    [Fact]
    public void Domain_event_valid_passes()
    {
        var t = DomainEvent("task.created", new JsonObject { ["field"] = "status", ["op"] = "eq", ["value"] = "open" });
        Assert.True(TriggerValidator.Validate(t).IsValid);
    }

    [Fact]
    public void Domain_event_invalid_type_is_detected()
    {
        var t = DomainEvent("not.a.real.event", new JsonObject { ["field"] = "status", ["op"] = "eq", ["value"] = "open" });
        Assert.Contains(TriggerValidator.Validate(t).Errors, e => e.Code == ValidationCodes.DomainEventTypeInvalid);
    }

    [Fact]
    public void Domain_event_invalid_filter_op_is_detected()
    {
        var t = DomainEvent("task.created", new JsonObject { ["field"] = "status", ["op"] = "bogus", ["value"] = "open" });
        Assert.Contains(TriggerValidator.Validate(t).Errors, e => e.Code == ValidationCodes.DomainEventFiltersInvalid);
    }

    [Fact]
    public void Domain_event_filter_value_too_long_is_detected()
    {
        var t = DomainEvent("task.created", new JsonObject { ["field"] = "status", ["op"] = "eq", ["value"] = new string('x', 201) });
        Assert.Contains(TriggerValidator.Validate(t).Errors, e => e.Code == ValidationCodes.DomainEventFiltersInvalid);
    }

    private static TriggerDefinition DomainEvent(string eventType, JsonObject filter) => new(
        "d", TriggerType.DomainEvent, true, null, null, null, null, null, null, null, null, null,
        eventType, new JsonArray(filter));
}
