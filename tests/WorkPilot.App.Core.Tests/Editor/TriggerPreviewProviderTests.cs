using System;
using WorkPilot.App.Core.Automation;
using WorkPilot.App.Core.Tests.Fakes;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation;
using Xunit;

namespace WorkPilot.App.Core.Tests.Editor;

public class TriggerPreviewProviderTests
{
    private static StubTimeZoneResolver Resolver() => new(new ConfigurableZone(TimeSpan.Zero));

    [Fact]
    public void Interval_preview_is_drift_free_and_counted()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new StubClock { UtcNow = now };
        var trigger = new TriggerDefinition("t", TriggerType.Interval, true, "UTC", null, null,
            3600, now, null, null, null, null, null, null);

        var items = TriggerPreviewProvider.ProjectNextOccurrences(trigger, clock, Resolver(), 10);

        Assert.Equal(10, items.Count);
        for (var i = 1; i < items.Count; i++)
            Assert.Equal(3600, (items[i].Utc - items[i - 1].Utc).TotalSeconds);
    }

    [Fact]
    public void Manual_trigger_has_no_background_schedule()
    {
        var clock = new StubClock { UtcNow = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) };
        var trigger = new TriggerDefinition("t", TriggerType.Manual, true, null, null, null, null, null, null, null, null, null, null, null);

        var items = TriggerPreviewProvider.ProjectNextOccurrences(trigger, clock, Resolver(), 10);
        Assert.Empty(items);
    }

    [Fact]
    public void Monthly_day_31_with_skip_annotates_missing_day()
    {
        var clock = new StubClock { UtcNow = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero) };
        var trigger = new TriggerDefinition("t", TriggerType.CalendarMonthly, true, "UTC", null, null,
            null, null, "09:00", null, 31, "skip", null, null);

        var items = TriggerPreviewProvider.ProjectNextOccurrences(trigger, clock, Resolver(), 12);

        Assert.NotEmpty(items);
        // February (and other short months) have no day 31, so the next occurrence is a skipped month.
        Assert.Contains(items, i => i.IsMissingDaySkipped);
    }

    [Fact]
    public void Count_of_zero_or_negative_yields_empty()
    {
        var clock = new StubClock { UtcNow = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) };
        var trigger = new TriggerDefinition("t", TriggerType.Interval, true, "UTC", null, null,
            3600, clock.UtcNow, null, null, null, null, null, null);
        Assert.Empty(TriggerPreviewProvider.ProjectNextOccurrences(trigger, clock, Resolver(), 0));
    }
}
