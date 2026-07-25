using System;
using System.Text.Json;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Infrastructure.Clock;
using Xunit;

namespace WorkPilot.Contracts.Tests;

public sealed class ClockTests
{
    private sealed class FakeClock : IClock
    {
        public FakeClock(DateTimeOffset fixedUtc) => UtcNow = fixedUtc;
        public DateTimeOffset UtcNow { get; }
        public DateTimeOffset Now => UtcNow.ToLocalTime();
    }

    [Fact]
    public void Timestamp_iso8601_round_trips()
    {
        var original = Timestamp.FromUtc(new DateTimeOffset(2026, 7, 21, 2, 44, 24, TimeSpan.Zero));
        var text = original.ToIso8601();
        var parsed = Timestamp.ParseIso8601(text);
        Assert.Equal(original, parsed);
    }

    [Fact]
    public void Timestamp_json_round_trips()
    {
        var original = Timestamp.FromUtc(new DateTimeOffset(2026, 7, 21, 2, 44, 24, TimeSpan.Zero));
        var json = JsonSerializer.Serialize(original);
        var back = JsonSerializer.Deserialize<Timestamp>(json);
        Assert.Equal(original, back);
    }

    [Fact]
    public void Timestamp_unix_seconds_round_trip()
    {
        var ts = Timestamp.FromUnixSeconds(1_750_000_000);
        Assert.Equal(1_750_000_000L, ts.ToUnixSeconds());
    }

    [Fact]
    public void Timestamp_respects_utc_normalization()
    {
        var local = new DateTimeOffset(2026, 7, 21, 10, 0, 0, TimeSpan.FromHours(8));
        var ts = Timestamp.FromUtc(local);
        Assert.Equal(TimeSpan.Zero, ts.Value.Offset);
        Assert.Equal(2, ts.Value.Hour);
    }

    [Fact]
    public void Timestamp_compares_and_adds()
    {
        var a = Timestamp.FromUnixSeconds(100);
        var b = Timestamp.FromUnixSeconds(200);
        Assert.True(a.CompareTo(b) < 0);
        Assert.True(b.CompareTo(a) > 0);
        Assert.Equal(Timestamp.FromUnixSeconds(150), a.Plus(TimeSpan.FromSeconds(50)));
        Assert.Equal(0, Timestamp.MinValue.CompareTo(Timestamp.MinValue));
    }

    [Fact]
    public void Timestamp_tryparse_rejects_garbage()
    {
        Assert.False(Timestamp.TryParseIso8601("not-a-time", out _));
        Assert.False(Timestamp.TryParseIso8601(null, out _));
        Assert.True(Timestamp.TryParseIso8601("2026-07-21T02:44:24.0000000Z", out _));
    }

    [Fact]
    public void Implicit_conversions_to_from_datetimeoffset()
    {
        DateTimeOffset dto = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Timestamp ts = dto;
        DateTimeOffset back = ts;
        Assert.Equal(dto, back);
    }

    [Fact]
    public void SystemClock_returns_near_now()
    {
        var clock = new SystemClock();
        var skew = Math.Abs((clock.UtcNow - DateTimeOffset.UtcNow).TotalSeconds);
        Assert.True(skew < 5, $"SystemClock skew too large: {skew}s");
    }

    [Fact]
    public void FakeClock_yields_deterministic_timestamp()
    {
        var fixedUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new FakeClock(fixedUtc);
        Assert.Equal(Timestamp.FromUtc(fixedUtc), Timestamp.FromUtc(clock.UtcNow));
    }
}
