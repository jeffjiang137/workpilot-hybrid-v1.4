namespace WorkPilot.Contracts.Primitives.Ids;

/// <summary>Strongly-typed identifier for a materialized trigger occurrence.</summary>
public readonly record struct TriggerOccurrenceId
{
    public string Value { get; }

    private TriggerOccurrenceId(string value) => Value = IdGuard.Normalize(value, "TriggerOccurrence");

    public static TriggerOccurrenceId Parse(string s) => new(s);

    public static bool TryParse(string? s, out TriggerOccurrenceId id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(s))
            return false;
        try
        {
            id = new TriggerOccurrenceId(s);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static TriggerOccurrenceId Create(IIdGenerator generator) => new(generator.NewId());

    public static implicit operator string(TriggerOccurrenceId id) => id.Value;

    public override string ToString() => Value;
}
