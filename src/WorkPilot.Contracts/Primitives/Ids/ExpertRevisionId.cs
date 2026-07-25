namespace WorkPilot.Contracts.Primitives.Ids;

/// <summary>Strongly-typed identifier for an immutable expert revision. Surfaces in contracts (spec §3).</summary>
public readonly record struct ExpertRevisionId
{
    public string Value { get; }

    private ExpertRevisionId(string value) => Value = IdGuard.Normalize(value, "ExpertRevision");

    public static ExpertRevisionId Parse(string s) => new(s);

    public static bool TryParse(string? s, out ExpertRevisionId id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(s))
            return false;
        try
        {
            id = new ExpertRevisionId(s);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static ExpertRevisionId Create(IIdGenerator generator) => new(generator.NewId());

    public static implicit operator string(ExpertRevisionId id) => id.Value;

    public override string ToString() => Value;
}
