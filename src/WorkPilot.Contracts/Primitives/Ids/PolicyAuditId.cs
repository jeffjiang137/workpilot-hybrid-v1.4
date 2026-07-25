namespace WorkPilot.Contracts.Primitives.Ids;

/// <summary>Strongly-typed identifier for a policy audit record (SEC-106).</summary>
public readonly record struct PolicyAuditId
{
    public string Value { get; }

    private PolicyAuditId(string value) => Value = IdGuard.Normalize(value, "PolicyAudit");

    public static PolicyAuditId Parse(string s) => new(s);

    public static bool TryParse(string? s, out PolicyAuditId id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(s))
            return false;
        try
        {
            id = new PolicyAuditId(s);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static PolicyAuditId Create(IIdGenerator generator) => new(generator.NewId());

    public static implicit operator string(PolicyAuditId id) => id.Value;

    public override string ToString() => Value;
}
