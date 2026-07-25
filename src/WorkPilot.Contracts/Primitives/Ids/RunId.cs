namespace WorkPilot.Contracts.Primitives.Ids;

/// <summary>Strongly-typed identifier for a durable automation run. Surfaces in contracts (spec §3/§5).</summary>
public readonly record struct RunId
{
    public string Value { get; }

    private RunId(string value) => Value = IdGuard.Normalize(value, "Run");

    public static RunId Parse(string s) => new(s);

    public static bool TryParse(string? s, out RunId id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(s))
            return false;
        try
        {
            id = new RunId(s);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static RunId Create(IIdGenerator generator) => new(generator.NewId());

    public static implicit operator string(RunId id) => id.Value;

    public override string ToString() => Value;
}
