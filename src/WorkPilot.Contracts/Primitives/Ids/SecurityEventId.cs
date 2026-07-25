namespace WorkPilot.Contracts.Primitives.Ids;

/// <summary>Strongly-typed identifier for a security event (doc 06 §2). Surfaces in contracts.</summary>
public readonly record struct SecurityEventId
{
    public string Value { get; }

    private SecurityEventId(string value) => Value = IdGuard.Normalize(value, "SecurityEvent");

    public static SecurityEventId Parse(string s) => new(s);

    public static bool TryParse(string? s, out SecurityEventId id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(s))
            return false;
        try
        {
            id = new SecurityEventId(s);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static SecurityEventId Create(IIdGenerator generator) => new(generator.NewId());

    public static implicit operator string(SecurityEventId id) => id.Value;

    public override string ToString() => Value;
}
