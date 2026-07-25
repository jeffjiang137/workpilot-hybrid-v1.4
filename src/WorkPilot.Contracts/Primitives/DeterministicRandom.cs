namespace WorkPilot.Contracts.Primitives;

/// <summary>
/// Deterministic, seedable PRNG (xorshift64*). Produces identical sequences for a given seed
/// across platforms — suitable for unit tests and reproducible scheduling jitter.
/// NOT cryptographically secure; use the Native crypto path for secrets.
/// </summary>
public sealed class DeterministicRandom : IRandomSource
{
    private ulong _state;

    public DeterministicRandom(ulong seed) => _state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;

    public DeterministicRandom() : this(0x9E3779B97F4A7C15UL)
    {
    }

    private ulong NextUInt64()
    {
        _state ^= _state >> 12;
        _state ^= _state << 25;
        _state ^= _state >> 27;
        return _state * 0x2545F4914F6CDD1DUL;
    }

    public int Next(int maxExclusive)
    {
        if (maxExclusive <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxExclusive), "Must be greater than zero.");
        return (int)(NextUInt64() % (ulong)maxExclusive);
    }

    public int Next(int minInclusive, int maxExclusive)
    {
        if (maxExclusive <= minInclusive)
            throw new ArgumentOutOfRangeException(nameof(maxExclusive), "Must be greater than minInclusive.");
        return minInclusive + Next(maxExclusive - minInclusive);
    }

    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

    public void NextBytes(byte[] buffer)
    {
        if (buffer is null)
            throw new ArgumentNullException(nameof(buffer));
        for (var i = 0; i < buffer.Length; i += 8)
        {
            var v = NextUInt64();
            for (var j = 0; j < 8 && i + j < buffer.Length; j++)
            {
                buffer[i + j] = (byte)(v & 0xFF);
                v >>= 8;
            }
        }
    }
}
