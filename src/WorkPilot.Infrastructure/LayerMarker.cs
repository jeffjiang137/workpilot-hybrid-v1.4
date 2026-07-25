namespace WorkPilot.Infrastructure;

/// <summary>
/// Layer identity marker for the Infrastructure layer. Infrastructure implements ports
/// defined by Application/Domain/Contracts and may depend on them, but must not depend
/// on the Host or the WinUI App.
/// </summary>
public static class LayerMarker
{
    public const string Name = "WorkPilot.Infrastructure";
}
