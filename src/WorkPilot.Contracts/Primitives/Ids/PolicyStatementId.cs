namespace WorkPilot.Contracts.Primitives.Ids;

/// <summary>Strongly-typed identifier for a policy statement within a version.</summary>
public readonly record struct PolicyStatementId
{
    public string Value { get; }

    private PolicyStatementId(string value) => Value = IdGuard.Normalize(value, "PolicyStatement");

    public static PolicyStatementId Parse(string s) => new(s);

    public static bool TryParse(string? s, out PolicyStatementId id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(s))
            return false;
        try
        {
            id = new PolicyStatementId(s);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static PolicyStatementId Create(IIdGenerator generator) => new(generator.NewId());

    public static implicit operator string(PolicyStatementId id) => id.Value;

    public override string ToString() => Value;
}
