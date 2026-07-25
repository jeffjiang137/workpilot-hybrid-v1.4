namespace WorkPilot.Contracts.Primitives.Ids;

/// <summary>Strongly-typed identifier for an automation. Surfaces in contracts (spec §5).</summary>
public readonly record struct AutomationId
{
    public string Value { get; }

    private AutomationId(string value) => Value = IdGuard.Normalize(value, "Automation");

    public static AutomationId Parse(string s) => new(s);

    public static bool TryParse(string? s, out AutomationId id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(s))
            return false;
        try
        {
            id = new AutomationId(s);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static AutomationId Create(IIdGenerator generator) => new(generator.NewId());

    public static implicit operator string(AutomationId id) => id.Value;

    public override string ToString() => Value;
}
