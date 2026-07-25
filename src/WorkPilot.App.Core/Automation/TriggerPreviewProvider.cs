using System.Collections.Generic;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Domain.Automation;
using WorkPilot.Domain.Automation.Scheduling;

namespace WorkPilot.App.Core.Automation;

/// <summary>
/// One projected trigger occurrence for the editor preview. Annotations come straight from the
/// shared T05 <see cref="ScheduleCalculator"/> output — the editor never computes occurrences itself.
/// </summary>
public sealed record TriggerPreviewItem(
    DateTimeOffset Utc,
    bool IsDstAdjustedForward,
    bool IsDstAmbiguousFirst,
    bool IsMissingDaySkipped);

/// <summary>
/// Projects the next occurrences of a trigger for the editor's "future 10 runs" preview. This is the
/// SAME <see cref="ScheduleCalculator.ComputeNext"/> the materializer uses (T05 DoD: "预览与调度共用同一算法").
/// It depends only on the injected <see cref="IClock"/> (for "now") and <see cref="ITimeZoneResolver"/>.
/// </summary>
public static class TriggerPreviewProvider
{
    /// <summary>
    /// Returns up to <paramref name="count"/> upcoming occurrences. Manual / domain-event triggers have
    /// no background schedule and yield an empty list. A monthly <c>missing_day=skip</c> trigger marks
    /// an item <see cref="TriggerPreviewItem.IsMissingDaySkipped"/> when the gap from the previous
    /// occurrence exceeds one nominal month (a skipped short month), detected from the shared algorithm's
    /// output — not a second scheduler.
    /// </summary>
    public static IReadOnlyList<TriggerPreviewItem> ProjectNextOccurrences(
        TriggerDefinition trigger, IClock clock, ITimeZoneResolver resolver, int count = 10)
    {
        if (trigger is null || clock is null || resolver is null)
            return System.Array.Empty<TriggerPreviewItem>();
        if (count <= 0)
            return System.Array.Empty<TriggerPreviewItem>();

        var items = new List<TriggerPreviewItem>(count);
        var after = clock.UtcNow;
        DateTimeOffset? previous = null;

        for (var i = 0; i < count; i++)
        {
            var result = ScheduleCalculator.ComputeNext(trigger, after, resolver);
            if (!result.HasOccurrence || result.Occurrence is null)
                break;

            var occ = result.Occurrence;
            var missingDaySkipped = trigger.Type == TriggerType.CalendarMonthly
                && trigger.MissingDay == TriggerEditorSession.MissingDaySkip
                && previous is { } prev
                && (occ.Utc - prev).TotalDays > 40;

            items.Add(new TriggerPreviewItem(occ.Utc, occ.DstAdjustedForward, occ.DstAmbiguousFirst, missingDaySkipped));
            previous = occ.Utc;
            after = occ.Utc.AddTicks(1); // advance past this tick for the next query
        }

        return items;
    }
}
