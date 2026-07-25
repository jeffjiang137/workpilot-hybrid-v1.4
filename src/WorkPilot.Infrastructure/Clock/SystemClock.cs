using System;
using WorkPilot.Contracts.Primitives;

namespace WorkPilot.Infrastructure.Clock;

/// <summary>Real system clock adapter. The only place that reads <c>DateTimeOffset.UtcNow</c>/<c>Now</c>.</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public DateTimeOffset Now => DateTimeOffset.Now;
}
