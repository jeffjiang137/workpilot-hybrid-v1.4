using System;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Infrastructure.Ids;
using Xunit;

namespace WorkPilot.Contracts.Tests;

public sealed class IdTests
{
    private sealed class MutableFakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; }
        public DateTimeOffset Now => UtcNow.ToLocalTime();
    }

    [Fact]
    public void Strongly_typed_id_parses_and_validates()
    {
        var id = AutomationId.Parse("auto-123_AB");
        Assert.Equal("auto-123_AB", id.Value);
        Assert.Equal("auto-123_AB", (string)id);
    }

    [Fact]
    public void Strongly_typed_id_rejects_invalid_input()
    {
        Assert.False(AutomationId.TryParse("has space", out _));
        Assert.False(AutomationId.TryParse("", out _));
        Assert.False(AutomationId.TryParse(null, out _));
        Assert.Throws<ArgumentException>(() => AutomationId.Parse("bad/char"));
    }

    [Fact]
    public void IdGuard_rejects_overlong_identifier()
    {
        var tooLong = new string('a', Limits.V1_5.MaxEntityIdLength + 1);
        Assert.Throws<ArgumentException>(() => AutomationId.Parse(tooLong));
    }

    [Fact]
    public void All_id_kinds_validate_consistently()
    {
        Assert.True(RunId.TryParse("run_1", out _));
        Assert.True(PolicyVersionId.TryParse("pv_9", out _));
        Assert.True(RevisionId.TryParse("rev_X", out _));
        Assert.False(RunId.TryParse("bad char", out _));
    }

    [Fact]
    public void DeterministicIdGenerator_is_monotonic()
    {
        var g = new DeterministicIdGenerator("t");
        var a = g.NewId();
        var b = g.NewId();
        Assert.NotEqual(a, b);
        Assert.StartsWith("t_", a);
    }

    [Fact]
    public void DeterministicIdGenerator_create_assigns_new_value()
    {
        var id = AutomationId.Create(new DeterministicIdGenerator());
        Assert.False(string.IsNullOrEmpty(id.Value));
    }

    [Fact]
    public void SortableIdGenerator_has_correct_shape()
    {
        var g = new SortableIdGenerator(new MutableFakeClock { UtcNow = DateTimeOffset.UnixEpoch }, new DeterministicRandom(1));
        var id = g.NewId();
        Assert.Equal(26, id.Length);
        Assert.All(id, c => Assert.True("0123456789ABCDEFGHJKMNPQRSTVWXYZ".Contains(c), $"bad char {c}"));
    }

    [Fact]
    public void SortableIdGenerator_is_time_ordered()
    {
        var clock = new MutableFakeClock { UtcNow = DateTimeOffset.UnixEpoch };
        var g = new SortableIdGenerator(clock, new DeterministicRandom(2));
        var first = g.NewId();
        clock.UtcNow = clock.UtcNow.AddSeconds(1);
        var second = g.NewId();
        Assert.True(string.CompareOrdinal(first, second) < 0, "Earlier timestamp should sort first.");
    }

    [Fact]
    public void SortableIdGenerator_is_deterministic_for_same_inputs()
    {
        var a = new SortableIdGenerator(new MutableFakeClock { UtcNow = DateTimeOffset.UnixEpoch }, new DeterministicRandom(42));
        var b = new SortableIdGenerator(new MutableFakeClock { UtcNow = DateTimeOffset.UnixEpoch }, new DeterministicRandom(42));
        Assert.Equal(a.NewId(), b.NewId());
    }
}
