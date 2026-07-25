namespace WorkPilot.Contracts.Primitives.Ids;

/// <summary>Strongly-typed identifier for an AutomationGrant (PER-004).</summary>
public readonly record struct PolicyGrantId
{
    public string Value { get; }

    private PolicyGrantId(string value) => Value = IdGuard.Normalize(value, "PolicyGrant");

    public static PolicyGrantId Parse(string s) => new(s);

    public static bool TryParse(string? s, out PolicyGrantId id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(s))
            return false;
        try
        {
            id = new PolicyGrantId(s);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static PolicyGrantId Create(IIdGenerator generator) => new(generator.NewId());

    public static implicit operator string(PolicyGrantId id) => id.Value;

    public override string ToString() => Value;
}
