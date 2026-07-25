namespace WorkPilot.Contracts.Primitives;

/// <summary>
/// Abstraction over randomness. New code MUST inject <see cref="IRandomSource"/> instead of
/// static <c>System.Random</c> so tests and reproducible runs are deterministic (AI dev rule §36).
/// </summary>
public interface IRandomSource
{
    int Next(int maxExclusive);
    int Next(int minInclusive, int maxExclusive);
    double NextDouble();
    void NextBytes(byte[] buffer);
}
