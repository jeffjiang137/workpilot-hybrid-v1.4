using System;
using System.Collections.Generic;
using System.Linq;
using WorkPilot.Domain.Automation.Scheduling;

namespace WorkPilot.Infrastructure.Automation.Scheduling;

/// <summary>
/// Production <see cref="IZone"/> backed by <see cref="TimeZoneInfo"/> (Windows time-zone ids, per
/// spec doc 04 §1). DST gap/ambiguity are resolved with <see cref="TimeZoneInfo.IsInvalidTime"/> /
/// <see cref="TimeZoneInfo.IsAmbiguousTime"/> / <see cref="TimeZoneInfo.GetAmbiguousTimeOffsets"/>,
/// producing 0/1/2 UTC candidates exactly as the <see cref="IZone"/> contract requires.
/// </summary>
public sealed class TimeZoneInfoZone : IZone
{
    private readonly TimeZoneInfo _tz;
    public string Id => _tz.Id;

    public TimeZoneInfoZone(TimeZoneInfo tz) => _tz = tz;

    public TimeSpan GetUtcOffset(DateTimeOffset utc) => _tz.GetUtcOffset(utc);

    // Returns the true UTC instant (offset 0) in the `Utc` field, with the local `Offset` kept
    // alongside. ScheduleCalculator consumes `Utc` directly as the scheduled instant, so it must be
    // normalized to offset 0 regardless of the zone's current offset (SCH-A02/A03).
    public IReadOnlyList<(DateTimeOffset Utc, TimeSpan Offset)> ResolveLocal(DateTime local)
    {
        var list = new List<(DateTimeOffset, TimeSpan)>();
        if (_tz.IsInvalidTime(local))
            return list; // spring-forward gap → 0 candidates

        if (_tz.IsAmbiguousTime(local))
        {
            // Fall-back: both offsets are valid; ordered by UTC ascending (the earlier UTC is first).
            foreach (var offset in _tz.GetAmbiguousTimeOffsets(local))
            {
                var utcInstant = local - offset;
                list.Add((new DateTimeOffset(utcInstant, TimeSpan.Zero), offset));
            }
            return list;
        }

        var offsetSingle = _tz.GetUtcOffset(local);
        var utcInstantSingle = local - offsetSingle;
        list.Add((new DateTimeOffset(utcInstantSingle, TimeSpan.Zero), offsetSingle));
        return list;
    }
}

/// <summary>Resolves a Windows time-zone id to a <see cref="TimeZoneInfoZone"/>. Returns null if unknown.</summary>
public sealed class WindowsTimeZoneResolver : ITimeZoneResolver
{
    public IZone? Resolve(string windowsTimeZoneId)
    {
        if (string.IsNullOrEmpty(windowsTimeZoneId)) return null;
        try
        {
            return new TimeZoneInfoZone(TimeZoneInfo.FindSystemTimeZoneById(windowsTimeZoneId));
        }
        catch (TimeZoneNotFoundException) { return null; }
        catch (InvalidTimeZoneException) { return null; }
    }
}
