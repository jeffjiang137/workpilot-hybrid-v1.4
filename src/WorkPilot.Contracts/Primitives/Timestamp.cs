using System;
using System.Globalization;

namespace WorkPilot.Contracts.Primitives;

/// <summary>
/// An instant on the UTC timeline. Always stored as UTC. Versioned value object used
/// everywhere a point in time is needed so scheduling/preview share one representation.
/// </summary>
public readonly record struct Timestamp : IComparable<Timestamp>, IComparable
{
    [System.Text.Json.Serialization.JsonInclude]
    public DateTimeOffset Value { get; init; }

    private Timestamp(DateTimeOffset value) => Value = value.ToUniversalTime();

    public static Timestamp FromUtc(DateTimeOffset value) => new(value);
    public static Timestamp FromUnixSeconds(long seconds) => new(DateTimeOffset.FromUnixTimeSeconds(seconds));
    public static Timestamp FromUnixMilliseconds(long milliseconds) => new(DateTimeOffset.FromUnixTimeMilliseconds(milliseconds));

    public long ToUnixSeconds() => Value.ToUnixTimeSeconds();
    public long ToUnixMilliseconds() => Value.ToUnixTimeMilliseconds();

    /// <summary>Round-trips via an invariant UTC ISO-8601 string (e.g. 2026-07-21T02:44:24.0000000Z).</summary>
    public string ToIso8601() => Value.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    public static Timestamp ParseIso8601(string s)
    {
        if (!TryParseIso8601(s, out var result))
            throw new FormatException($"Cannot parse timestamp: '{s}'.");
        return result;
    }

    public static bool TryParseIso8601(string? s, out Timestamp result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(s))
            return false;
        if (DateTimeOffset.TryParse(
                s,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            result = new Timestamp(parsed);
            return true;
        }
        return false;
    }

    public static Timestamp MinValue => new(DateTimeOffset.MinValue);
    public static Timestamp MaxValue => new(DateTimeOffset.MaxValue);

    public Timestamp Plus(TimeSpan delta) => new(Value + delta);

    public int CompareTo(Timestamp other) => Value.CompareTo(other.Value);

    public int CompareTo(object? obj) =>
        obj is Timestamp t ? CompareTo(t) : throw new ArgumentException("Object is not a Timestamp.", nameof(obj));

    public static implicit operator DateTimeOffset(Timestamp ts) => ts.Value;
    public static implicit operator Timestamp(DateTimeOffset dto) => new(dto);

    public override string ToString() => ToIso8601();
}
