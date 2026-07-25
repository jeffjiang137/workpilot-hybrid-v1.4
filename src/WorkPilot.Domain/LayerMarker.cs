namespace WorkPilot.Domain;

/// <summary>
/// Layer identity marker for the Domain layer. Domain holds pure business rules and
/// must not depend on Application, Infrastructure, or Host.
/// </summary>
public static class LayerMarker
{
    public const string Name = "WorkPilot.Domain";
}
