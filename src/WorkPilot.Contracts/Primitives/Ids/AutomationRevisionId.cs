namespace WorkPilot.Contracts.Primitives.Ids;

/// <summary>Strongly-typed identifier for an immutable automation revision. Surfaces in contracts.</summary>
public readonly record struct AutomationRevisionId
{
    public string Value { get; }

    private AutomationRevisionId(string value) => Value = IdGuard.Normalize(value, "AutomationRevision");

    public static AutomationRevisionId Parse(string s) => new(s);

    public static bool TryParse(string? s, out AutomationRevisionId id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(s))
            return false;
        try
        {
            id = new AutomationRevisionId(s);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static AutomationRevisionId Create(IIdGenerator generator) => new(generator.NewId());

    public static implicit operator string(AutomationRevisionId id) => id.Value;

    public override string ToString() => Value;
}
