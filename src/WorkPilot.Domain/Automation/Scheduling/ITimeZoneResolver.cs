namespace WorkPilot.Domain.Automation.Scheduling;

/// <summary>
/// Abstraction over a time zone. Implementations map local wall-clock time to UTC. The production
/// implementation (in Infrastructure) wraps <c>TimeZoneInfo</c>; tests inject a synthetic zone so
/// DST behavior is deterministic and OS-independent (AI dev rule §124: clock/timezone must be replaceable).
/// </summary>
public interface IZone
{
    /// <summary>Stable identifier (e.g. Windows <c>TimeZoneInfo.Id</c>).</summary>
    string Id { get; }

    /// <summary>UTC offset applicable at the given UTC instant.</summary>
    TimeSpan GetUtcOffset(DateTimeOffset utc);

    /// <summary>
    /// Resolve a local (wall-clock, unspecified-kind) date/time to the UTC instant(s) it can map to.
    /// Returns:
    /// <list type="bullet">
    ///   <item>0 entries — the local time is invalid (spring-forward gap);</item>
    ///   <item>1 entry — a normal, unambiguous local time;</item>
    ///   <item>2 entries — an ambiguous local time (fall-back), ordered by UTC ascending.</item>
    /// </list>
    /// </summary>
    IReadOnlyList<(DateTimeOffset Utc, TimeSpan Offset)> ResolveLocal(DateTime local);
}

/// <summary>Resolves a stored Windows time-zone id to an <see cref="IZone"/>. Returns null if unknown.</summary>
public interface ITimeZoneResolver
{
    IZone? Resolve(string windowsTimeZoneId);
}
