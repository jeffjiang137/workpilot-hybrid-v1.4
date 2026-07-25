using System;
using WorkPilot.App.Core.Automation;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Domain.Automation;
using WorkPilot.Domain.Automation.Validation;
using Xunit;

namespace WorkPilot.App.Core.Tests.Editor;

public class TriggerEditorSessionTests
{
    private static DateTimeOffset Now() => new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ChangeType_interval_seeds_valid_defaults()
    {
        var t = new TriggerEditorSession(new TriggerDefinition("t", TriggerType.Manual, true, null, null, null, null, null, null, null, null, null, null, null));
        t.ChangeType(TriggerType.Interval, Now());

        Assert.Equal(TriggerType.Interval, t.Type);
        Assert.Equal(Limits.V1_5.MinIntervalSeconds, t.IntervalSeconds);
        Assert.NotNull(t.AnchorAtUtc);
        Assert.False(t.Validation.HasErrors);
    }

    [Fact]
    public void Interval_below_minimum_is_invalid()
    {
        var t = new TriggerEditorSession(new TriggerDefinition("t", TriggerType.Interval, true, "UTC", null, null,
            Limits.V1_5.MinIntervalSeconds, Now(), null, null, null, null, null, null));
        Assert.False(t.Validation.HasErrors);

        t.IntervalSeconds = 30; // below minimum
        Assert.True(t.Validation.HasErrors);
    }

    [Fact]
    public void Interval_within_bounds_is_valid()
    {
        var t = new TriggerEditorSession(new TriggerDefinition("t", TriggerType.Interval, true, "UTC", null, null,
            3600, Now(), null, null, null, null, null, null));
        Assert.False(t.Validation.HasErrors);
    }

    [Fact]
    public void ChangeType_manual_produces_manual_trigger()
    {
        var t = new TriggerEditorSession(new TriggerDefinition("t", TriggerType.Interval, true, "UTC", null, null,
            3600, Now(), null, null, null, null, null, null));
        t.ChangeType(TriggerType.Manual);
        Assert.Equal(TriggerType.Manual, t.Type);
        Assert.Null(t.TimezoneId);
    }

    [Fact]
    public void ChangeType_monthly_seeds_day_and_missing_day_policy()
    {
        var t = new TriggerEditorSession(new TriggerDefinition("t", TriggerType.Manual, true, null, null, null, null, null, null, null, null, null, null, null));
        t.ChangeType(TriggerType.CalendarMonthly);
        Assert.Equal(TriggerType.CalendarMonthly, t.Type);
        Assert.Equal(1, t.DayOfMonth);
        Assert.Equal(TriggerEditorSession.MissingDaySkip, t.MissingDay);
        Assert.Equal("09:00", t.LocalTime);
    }

    [Fact]
    public void Validation_changes_when_trigger_mutates()
    {
        var t = new TriggerEditorSession(new TriggerDefinition("t", TriggerType.Interval, true, "UTC", null, null,
            Limits.V1_5.MinIntervalSeconds, Now(), null, null, null, null, null, null));
        var before = t.Validation;
        t.IntervalSeconds = Limits.V1_5.MaxIntervalSeconds + 1; // above maximum
        Assert.True(t.Validation.HasErrors);
        Assert.NotEqual(before, t.Validation);
    }
}
