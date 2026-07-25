namespace WorkPilot.Contracts.Primitives.Ids;

/// <summary>Strongly-typed identifier for a structured run event.</summary>
public readonly record struct RunEventId
{
    public string Value { get; }

    private RunEventId(string value) => Value = IdGuard.Normalize(value, "RunEvent");

    public static RunEventId Parse(string s) => new(s);

    public static bool TryParse(string? s, out RunEventId id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(s))
            return false;
        try
        {
            id = new RunEventId(s);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static RunEventId Create(IIdGenerator generator) => new(generator.NewId());

    public static implicit operator string(RunEventId id) => id.Value;

    public override string ToString() => Value;
}
