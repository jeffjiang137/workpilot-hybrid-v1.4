namespace WorkPilot.Contracts.Primitives;

/// <summary>
/// Monotonic, predictable IDs for tests and reproducible executions. Not for production
/// uniqueness — use <c>SortableIdGenerator</c> (Infrastructure) in real runs.
/// </summary>
public sealed class DeterministicIdGenerator : IIdGenerator
{
    private long _counter;
    private readonly string _prefix;

    public DeterministicIdGenerator(string prefix = "id") => _prefix = prefix;

    public string NewId() => $"{_prefix}_{++_counter:x}";
}
