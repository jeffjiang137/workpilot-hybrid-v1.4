namespace WorkPilot.Contracts.Primitives.Ids;

/// <summary>Strongly-typed identifier for an aggregated security incident (doc 06 §3).</summary>
public readonly record struct IncidentId
{
    public string Value { get; }

    private IncidentId(string value) => Value = IdGuard.Normalize(value, "Incident");

    public static IncidentId Parse(string s) => new(s);

    public static bool TryParse(string? s, out IncidentId id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(s))
            return false;
        try
        {
            id = new IncidentId(s);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static IncidentId Create(IIdGenerator generator) => new(generator.NewId());

    public static implicit operator string(IncidentId id) => id.Value;

    public override string ToString() => Value;
}
