using System;
using System.Collections.Generic;
using WorkPilot.Domain.Automation.Scheduling;

namespace WorkPilot.App.Core.Tests.Fakes;

/// <summary>
/// Deterministic time zone for tests. Applies a fixed UTC offset; optionally simulates a spring-forward
/// gap (local times in [gapStart, gapStart+1h) map to zero candidates). Resolved UTC instants are always
/// normalized to offset 0, matching the production <see cref="IZone"/> contract fixed in T05.
/// </summary>
public sealed class ConfigurableZone : IZone
{
    private readonly TimeSpan _offset;
    private readonly DateTime? _gapStart;

    public ConfigurableZone(TimeSpan offset, DateTime? gapStart = null)
    {
        _offset = offset;
        _gapStart = gapStart;
    }

    public string Id => "stub";

    public TimeSpan GetUtcOffset(DateTimeOffset utc) => _offset;

    public IReadOnlyList<(DateTimeOffset Utc, TimeSpan Offset)> ResolveLocal(DateTime local)
    {
        if (_gapStart is { } g && local >= g && local < g.AddHours(1))
            return Array.Empty<(DateTimeOffset, TimeSpan)>();

        var instant = DateTime.SpecifyKind(local - _offset, DateTimeKind.Unspecified);
        var utc = new DateTimeOffset(instant, TimeSpan.Zero);
        return new[] { (utc, _offset) };
    }
}

/// <summary>Resolver that always returns the same injected zone (id is ignored).</summary>
public sealed class StubTimeZoneResolver : ITimeZoneResolver
{
    private readonly IZone _zone;
    public StubTimeZoneResolver(IZone zone) => _zone = zone;
    public IZone? Resolve(string windowsTimeZoneId) => _zone;
}
