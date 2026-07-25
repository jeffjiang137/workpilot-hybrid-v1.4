using System;
using WorkPilot.Contracts.Primitives;

namespace WorkPilot.Infrastructure.Ids;

/// <summary>
/// Time-ordered, lexicographically sortable ID generator (ULID-style, Crockford base32).
/// Monotonic per instance; deterministic given a fixed clock + random for tests. Suitable for
/// production primary keys and for cursor/log ordering.
/// </summary>
public sealed class SortableIdGenerator : IIdGenerator
{
    private static readonly char[] Encoding = "0123456789ABCDEFGHJKMNPQRSTVWXYZ".ToCharArray();
    private readonly IClock _clock;
    private readonly IRandomSource _random;
    private readonly object _sync = new();
    private long _lastMs;

    public SortableIdGenerator(IClock clock, IRandomSource random)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _random = random ?? throw new ArgumentNullException(nameof(random));
    }

    public string NewId()
    {
        var now = _clock.UtcNow.ToUnixTimeMilliseconds();
        lock (_sync)
        {
            if (now <= _lastMs)
                now = _lastMs + 1;
            _lastMs = now;
        }

        Span<char> buf = stackalloc char[26];
        var time = now;
        for (var i = 9; i >= 0; i--)
        {
            buf[i] = Encoding[(int)(time % 32)];
            time /= 32;
        }

        for (var i = 10; i < 26; i++)
        {
            buf[i] = Encoding[_random.Next(32)];
        }

        return new string(buf);
    }
}
