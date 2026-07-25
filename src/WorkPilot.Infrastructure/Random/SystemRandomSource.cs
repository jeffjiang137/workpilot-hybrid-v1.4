using WorkPilot.Contracts.Primitives;

namespace WorkPilot.Infrastructure.Random;

/// <summary>Real randomness adapter backed by <see cref="System.Random"/>.</summary>
public sealed class SystemRandomSource : IRandomSource
{
    private readonly System.Random _rng = new();

    public int Next(int maxExclusive) => _rng.Next(maxExclusive);
    public int Next(int minInclusive, int maxExclusive) => _rng.Next(minInclusive, maxExclusive);
    public double NextDouble() => _rng.NextDouble();
    public void NextBytes(byte[] buffer) => _rng.NextBytes(buffer);
}
