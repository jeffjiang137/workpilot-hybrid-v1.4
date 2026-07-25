namespace WorkPilot.Contracts.Primitives.Ids;

/// <summary>Strongly-typed identifier for a space (tenant/project boundary). Surfaces in contracts.</summary>
public readonly record struct SpaceId
{
    public string Value { get; }

    private SpaceId(string value) => Value = IdGuard.Normalize(value, "Space");

    public static SpaceId Parse(string s) => new(s);

    public static bool TryParse(string? s, out SpaceId id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(s))
            return false;
        try
        {
            id = new SpaceId(s);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static SpaceId Create(IIdGenerator generator) => new(generator.NewId());

    public static implicit operator string(SpaceId id) => id.Value;

    public override string ToString() => Value;
}
