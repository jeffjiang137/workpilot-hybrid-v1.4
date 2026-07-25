using System;
using System.Collections.Generic;
using WorkPilot.Domain.Automation.Scheduling;

namespace WorkPilot.Domain.Tests.Scheduling;

/// <summary>
/// Deterministic, OS-independent time zone for DST tests. Models standard/daylight offsets and two
/// UTC transition instants (spring-forward / fall-back) exactly like a real tz database, so the
/// scheduler's gap/ambiguity logic is exercised without depending on the host's tz data.
/// </summary>
internal sealed class SyntheticZone : IZone
{
    public string Id { get; }
    private readonly TimeSpan _standard;
    private readonly TimeSpan _daylight;
    private readonly DateTimeOffset _dstStartUtc;
    private readonly DateTimeOffset _dstEndUtc;

    public SyntheticZone(string id, TimeSpan standard, TimeSpan daylight,
        DateTimeOffset dstStartUtc, DateTimeOffset dstEndUtc)
    {
        Id = id;
        _standard = standard;
        _daylight = daylight;
        _dstStartUtc = dstStartUtc;
        _dstEndUtc = dstEndUtc;
    }

    public TimeSpan GetUtcOffset(DateTimeOffset utc) => InDst(utc) ? _daylight : _standard;

    private bool InDst(DateTimeOffset utc) => utc >= _dstStartUtc && utc < _dstEndUtc;

    // Returns the true UTC instant (offset 0) in the `Utc` field, with the local `Offset` kept
    // alongside. ScheduleCalculator consumes `Utc` directly as the scheduled instant, so it must be
    // normalized to offset 0 regardless of the zone's standard/daylight offset (SCH-A02/A03).
    public IReadOnlyList<(DateTimeOffset Utc, TimeSpan Offset)> ResolveLocal(DateTime local)
    {
        var candidates = new List<(DateTimeOffset, TimeSpan)>();
        var utcStdInstant = local - _standard;
        if (GetUtcOffset(new DateTimeOffset(utcStdInstant, TimeSpan.Zero)) == _standard)
            candidates.Add((new DateTimeOffset(utcStdInstant, TimeSpan.Zero), _standard));
        var utcDstInstant = local - _daylight;
        if (GetUtcOffset(new DateTimeOffset(utcDstInstant, TimeSpan.Zero)) == _daylight)
            candidates.Add((new DateTimeOffset(utcDstInstant, TimeSpan.Zero), _daylight));
        return candidates;
    }
}

/// <summary>Returns the same synthetic zone for any id (so tests ignore the stored id).</summary>
internal sealed class AnyZoneResolver : ITimeZoneResolver
{
    private readonly IZone _zone;
    public AnyZoneResolver(IZone zone) => _zone = zone;
    public IZone? Resolve(string _) => _zone;
}

/// <summary>Models an unknown/removed time zone (SCH-A07).</summary>
internal sealed class NullZoneResolver : ITimeZoneResolver
{
    public IZone? Resolve(string _) => null;
}

internal static class ZoneFactory
{
    /// <summary>Standard +0 / daylight +1, spring-forward at local 03:00 on 2026-03-29, fall-back at local 02:00 on 2026-10-25.</summary>
    public static SyntheticZone EuLike() => new(
        "TestZone",
        TimeSpan.Zero,
        TimeSpan.FromHours(1),
        new DateTimeOffset(2026, 3, 29, 2, 0, 0, TimeSpan.Zero),   // offset -> +1h here (local jumps 02:00 -> 03:00)
        new DateTimeOffset(2026, 10, 25, 2, 0, 0, TimeSpan.Zero));  // offset -> 0 here (local falls back 03:00 -> 02:00)
}
