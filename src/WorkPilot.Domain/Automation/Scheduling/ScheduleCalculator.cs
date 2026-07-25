using System.Globalization;
using WorkPilot.Contracts.Primitives;

namespace WorkPilot.Domain.Automation.Scheduling;

/// <summary>
/// Pure next-occurrence calculator (spec doc 04 §2). The single source of truth for "when does this
/// trigger fire next", shared by the editor preview and the materializer (T05 DoD). Depends only on
/// the injected <see cref="ITimeZoneResolver"/> and tick math — no I/O, no static clock. Interval is
/// anchor-based and drift-free (SCH-A01); calendar handles DST, missing months, and leap years.
/// </summary>
public static class ScheduleCalculator
{
    public static NextOccurrenceResult ComputeNext(
        TriggerDefinition trigger, DateTimeOffset afterUtc, ITimeZoneResolver resolver)
    {
        switch (trigger.Type)
        {
            case TriggerType.Manual:
            case TriggerType.DomainEvent:
                return NextOccurrenceResult.NoScheduledTime();
            case TriggerType.Interval:
                return ComputeInterval(trigger, afterUtc);
            case TriggerType.Once:
                return ComputeOnce(trigger, afterUtc);
            default: // calendar_daily / calendar_weekly / calendar_monthly
                return ComputeCalendar(trigger, afterUtc, resolver);
        }
    }

    private static NextOccurrenceResult ComputeInterval(TriggerDefinition t, DateTimeOffset afterUtc)
    {
        if (t.IntervalSeconds is not { } secs || secs < Limits.V1_5.MinIntervalSeconds || secs > Limits.V1_5.MaxIntervalSeconds)
            return NextOccurrenceResult.Error(SchedulingCodes.IntervalInvalid, D("value", (t.IntervalSeconds ?? -1).ToString()));
        if (t.AnchorAtUtc is not { } anchor)
            return NextOccurrenceResult.Error(SchedulingCodes.IntervalInvalid, D("reason", "anchor_missing"));

        DateTimeOffset next;
        if (afterUtc < anchor)
        {
            next = anchor;
        }
        else
        {
            // Integer tick math: never drifts from the anchor regardless of failures/pauses (SCH-A01).
            var deltaTicks = afterUtc.UtcTicks - anchor.UtcTicks;
            var intervalTicks = secs * TimeSpan.TicksPerSecond;
            var n = (deltaTicks / intervalTicks) + 1; // strictly after `afterUtc`
            next = new DateTimeOffset(anchor.UtcTicks + n * intervalTicks, TimeSpan.Zero);
        }
        if (next <= afterUtc)
            return NextOccurrenceResult.NoScheduledTime();
        return NextOccurrenceResult.Found(new ScheduledOccurrence(next, false, false));
    }

    private static NextOccurrenceResult ComputeOnce(TriggerDefinition t, DateTimeOffset afterUtc)
    {
        if (t.StartAtUtc is not { } start)
            return NextOccurrenceResult.NoScheduledTime();
        return start > afterUtc
            ? NextOccurrenceResult.Found(new ScheduledOccurrence(start, false, false))
            : NextOccurrenceResult.NoScheduledTime();
    }

    private static NextOccurrenceResult ComputeCalendar(
        TriggerDefinition t, DateTimeOffset afterUtc, ITimeZoneResolver resolver)
    {
        if (string.IsNullOrEmpty(t.TimezoneId) || resolver.Resolve(t.TimezoneId) is not { } zone)
            return NextOccurrenceResult.Error(SchedulingCodes.TimezoneNotFound, D("timezone_id", t.TimezoneId ?? "null"));
        if (!TimeSpan.TryParseExact(t.LocalTime, "hh\\:mm", CultureInfo.InvariantCulture, out var lt))
            return NextOccurrenceResult.Error(SchedulingCodes.CalendarTimeInvalid, D("local_time", t.LocalTime ?? "null"));
        var hour = lt.Hours;
        var minute = lt.Minutes;

        var localAfter = afterUtc + zone.GetUtcOffset(afterUtc);
        var startDate = localAfter.DateTime.Date;
        var horizonEnd = startDate.AddYears(Limits.V1_5.MaxCalendarHorizonYears);

        if (t.Type == TriggerType.CalendarMonthly)
        {
            for (var ym = new DateTime(startDate.Year, startDate.Month, 1); ym <= horizonEnd; ym = ym.AddMonths(1))
            {
                if (!TryCandidateDate(t, ym.Year, ym.Month, zone, hour, minute, afterUtc, out var occ))
                    continue;
                return NextOccurrenceResult.Found(occ);
            }
        }
        else
        {
            for (var d = startDate; d <= horizonEnd; d = d.AddDays(1))
            {
                if (t.Type is TriggerType.CalendarDaily or TriggerType.CalendarWeekly)
                {
                    if (t.DaysOfWeek is not { } days || !days.Contains((int)d.DayOfWeek))
                        continue;
                }
                if (!TryResolveLocalTime(zone, d.Year, d.Month, d.Day, hour, minute,
                        t.StartAtUtc, t.EndAtUtc, afterUtc, out var occ))
                    continue;
                return NextOccurrenceResult.Found(occ);
            }
        }
        return NextOccurrenceResult.NoScheduledTime();
    }

    /// <summary>Monthly variant: resolves the effective day-of-month (handles <c>last</c> / missing-day).</summary>
    private static bool TryCandidateDate(
        TriggerDefinition t, int year, int month, IZone zone, int hour, int minute,
        DateTimeOffset afterUtc, out ScheduledOccurrence occ)
    {
        var daysInMonth = DateTime.DaysInMonth(year, month);
        int? effectiveDay = t.DayOfMonth ?? (t.MissingDay == "last_day" ? daysInMonth : (int?)null);
        if (effectiveDay is null) { occ = null!; return false; }
        if (effectiveDay > daysInMonth)
        {
            if (t.MissingDay == "last_day") effectiveDay = daysInMonth;
            else { occ = null!; return false; } // skip this month (SCH-A04)
        }
        return TryResolveLocalTime(zone, year, month, effectiveDay.Value, hour, minute,
            t.StartAtUtc, t.EndAtUtc, afterUtc, out occ);
    }

    private static bool TryResolveLocalTime(
        IZone zone, int year, int month, int day, int hour, int minute,
        DateTimeOffset? startAtUtc, DateTimeOffset? endAtUtc,
        DateTimeOffset afterUtc, out ScheduledOccurrence occ)
    {
        var local = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        var candidates = zone.ResolveLocal(local);

        DateTimeOffset chosen = default;
        bool dstForward = false, dstAmbiguous = false;

        if (candidates.Count == 0)
        {
            // Spring-forward gap: move forward minute-by-minute to the first valid local time (SCH-A02).
            bool resolved = false;
            for (var m = 1; m <= 120; m++)
            {
                var c2 = zone.ResolveLocal(local.AddMinutes(m));
                if (c2.Count > 0) { chosen = c2[0].Utc; dstForward = true; resolved = true; break; }
            }
            if (!resolved) { occ = null!; return false; }
        }
        else if (candidates.Count == 2)
        {
            // Fall-back ambiguity: choose the earlier UTC instance, run once (SCH-A03).
            chosen = candidates.OrderBy(c => c.Utc).First().Utc;
            dstAmbiguous = true;
        }
        else
        {
            chosen = candidates[0].Utc;
        }

        if (chosen <= afterUtc) { occ = null!; return false; }
        if (startAtUtc is { } s && chosen < s) { occ = null!; return false; }
        if (endAtUtc is { } e && chosen > e) { occ = null!; return false; }
        occ = new ScheduledOccurrence(chosen, dstForward, dstAmbiguous);
        return true;
    }

    private static IReadOnlyDictionary<string, string> D(string k, string v)
        => new Dictionary<string, string> { [k] = v };
}
