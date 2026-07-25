namespace WorkPilot.Contracts.Primitives.Ids;

/// <summary>Strongly-typed identifier for a policy version. Surfaces in contracts (spec §5).</summary>
public readonly record struct PolicyVersionId
{
    public string Value { get; }

    private PolicyVersionId(string value) => Value = IdGuard.Normalize(value, "PolicyVersion");

    public static PolicyVersionId Parse(string s) => new(s);

    public static bool TryParse(string? s, out PolicyVersionId id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(s))
            return false;
        try
        {
            id = new PolicyVersionId(s);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static PolicyVersionId Create(IIdGenerator generator) => new(generator.NewId());

    public static implicit operator string(PolicyVersionId id) => id.Value;

    public override string ToString() => Value;
}
