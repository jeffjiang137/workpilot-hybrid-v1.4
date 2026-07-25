using WorkPilot.Contracts.Primitives;

namespace WorkPilot.App.Core.Tests.Fakes;

/// <summary>Monotonic id generator for deterministic tests (AI dev rule §124).</summary>
public sealed class SeqIdGenerator : IIdGenerator
{
    private int _n;
    public string NewId() => $"id_{++_n:D6}";
}
