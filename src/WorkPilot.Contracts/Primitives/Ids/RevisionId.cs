namespace WorkPilot.Contracts.Primitives.Ids;

/// <summary>Strongly-typed identifier for an immutable automation revision (spec §4).</summary>
public readonly record struct RevisionId
{
    public string Value { get; }

    private RevisionId(string value) => Value = IdGuard.Normalize(value, "Revision");

    public static RevisionId Parse(string s) => new(s);

    public static bool TryParse(string? s, out RevisionId id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(s))
            return false;
        try
        {
            id = new RevisionId(s);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static RevisionId Create(IIdGenerator generator) => new(generator.NewId());

    public static implicit operator string(RevisionId id) => id.Value;

    public override string ToString() => Value;
}
