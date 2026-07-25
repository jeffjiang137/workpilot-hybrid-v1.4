using System;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;

namespace WorkPilot.Domain.Tests;

internal sealed class FakeClock(DateTimeOffset fixedTime) : IClock
{
    public DateTimeOffset UtcNow => fixedTime;
    public DateTimeOffset Now => fixedTime;
}

/// <summary>Deterministic id generator producing sortable, stable ids for tests.</summary>
internal sealed class SequentialIdGenerator : IIdGenerator
{
    private int _counter;
    public string NewId() => $"id_{++_counter:000000}";
}
