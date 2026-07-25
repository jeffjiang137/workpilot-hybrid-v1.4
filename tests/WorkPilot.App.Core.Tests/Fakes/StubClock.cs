using WorkPilot.Contracts.Primitives;

namespace WorkPilot.App.Core.Tests.Fakes;

/// <summary>Deterministic, settable clock for tests (AI dev rule §124: clock must be replaceable).</summary>
public sealed class StubClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public DateTimeOffset Now => UtcNow;
}
