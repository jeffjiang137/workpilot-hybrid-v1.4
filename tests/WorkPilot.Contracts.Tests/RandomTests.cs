using WorkPilot.Contracts.Primitives;
using WorkPilot.Infrastructure.Random;
using Xunit;

namespace WorkPilot.Contracts.Tests;

public sealed class RandomTests
{
    [Fact]
    public void DeterministicRandom_is_reproducible_for_same_seed()
    {
        var a = new DeterministicRandom(12345UL);
        var b = new DeterministicRandom(12345UL);
        for (var i = 0; i < 100; i++)
            Assert.Equal(a.Next(1000), b.Next(1000));
    }

    [Fact]
    public void DeterministicRandom_next_upper_bound_is_exclusive()
    {
        var r = new DeterministicRandom(7UL);
        for (var i = 0; i < 1000; i++)
        {
            var v = r.Next(50);
            Assert.InRange(v, 0, 49);
        }
    }

    [Fact]
    public void DeterministicRandom_next_range_is_inclusive_exclusive()
    {
        var r = new DeterministicRandom(99UL);
        for (var i = 0; i < 1000; i++)
        {
            var v = r.Next(10, 20);
            Assert.InRange(v, 10, 19);
        }
    }

    [Fact]
    public void DeterministicRandom_next_double_is_in_unit_interval()
    {
        var r = new DeterministicRandom(3UL);
        for (var i = 0; i < 1000; i++)
        {
            var v = r.NextDouble();
            Assert.InRange(v, 0.0, 1.0);
            Assert.False(double.IsNaN(v));
        }
    }

    [Fact]
    public void DeterministicRandom_fills_bytes()
    {
        var r = new DeterministicRandom(555UL);
        var buf = new byte[64];
        r.NextBytes(buf);
        Assert.Contains(buf, x => x != 0);
    }

    [Fact]
    public void DeterministicRandom_different_seeds_diverge()
    {
        var a = new DeterministicRandom(1UL).Next(1_000_000);
        var b = new DeterministicRandom(2UL).Next(1_000_000);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void SystemRandomSource_stays_in_bounds()
    {
        var r = new SystemRandomSource();
        for (var i = 0; i < 500; i++)
        {
            Assert.InRange(r.Next(100), 0, 99);
            Assert.InRange(r.NextDouble(), 0.0, 1.0);
        }
    }
}
