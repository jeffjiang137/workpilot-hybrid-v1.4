namespace WorkPilot.Contracts.Primitives.Ids;

/// <summary>Strongly-typed identifier for a policy document (one per layer+scope).</summary>
public readonly record struct PolicyDocumentId
{
    public string Value { get; }

    private PolicyDocumentId(string value) => Value = IdGuard.Normalize(value, "PolicyDocument");

    public static PolicyDocumentId Parse(string s) => new(s);

    public static bool TryParse(string? s, out PolicyDocumentId id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(s))
            return false;
        try
        {
            id = new PolicyDocumentId(s);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static PolicyDocumentId Create(IIdGenerator generator) => new(generator.NewId());

    public static implicit operator string(PolicyDocumentId id) => id.Value;

    public override string ToString() => Value;
}
