namespace WorkPilot.Contracts.Primitives.Ids;

/// <summary>Strongly-typed identifier for a single step execution within a run.</summary>
public readonly record struct StepRunId
{
    public string Value { get; }

    private StepRunId(string value) => Value = IdGuard.Normalize(value, "StepRun");

    public static StepRunId Parse(string s) => new(s);

    public static bool TryParse(string? s, out StepRunId id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(s))
            return false;
        try
        {
            id = new StepRunId(s);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static StepRunId Create(IIdGenerator generator) => new(generator.NewId());

    public static implicit operator string(StepRunId id) => id.Value;

    public override string ToString() => Value;
}
