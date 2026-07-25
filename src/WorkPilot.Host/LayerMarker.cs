namespace WorkPilot.Host;

/// <summary>
/// Layer identity marker for the background Host layer. The Host is the non-UI process
/// host (scheduler/runner, T08+). It composes Application+Infrastructure but must NEVER
/// reference WinUI or the WorkPilot.App (WinUI) assembly.
/// </summary>
public static class LayerMarker
{
    public const string Name = "WorkPilot.Host";
}
