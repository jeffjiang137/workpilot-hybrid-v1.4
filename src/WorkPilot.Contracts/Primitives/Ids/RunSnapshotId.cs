namespace WorkPilot.Contracts.Primitives.Ids;

/// <summary>Strongly-typed identifier for a frozen run snapshot (definition/expert/capability/policy).</summary>
public readonly record struct RunSnapshotId
{
    public string Value { get; }

    private RunSnapshotId(string value) => Value = IdGuard.Normalize(value, "RunSnapshot");

    public static RunSnapshotId Parse(string s) => new(s);

    public static bool TryParse(string? s, out RunSnapshotId id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(s))
            return false;
        try
        {
            id = new RunSnapshotId(s);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static RunSnapshotId Create(IIdGenerator generator) => new(generator.NewId());

    public static implicit operator string(RunSnapshotId id) => id.Value;

    public override string ToString() => Value;
}
