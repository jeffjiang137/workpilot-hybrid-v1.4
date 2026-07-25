namespace WorkPilot.Contracts.Primitives;

/// <summary>
/// Stable error categories. Third-party bodies are mapped into these categories; they never
/// leak into the message or safe details (AI dev rule §13).
/// </summary>
public enum ErrorCategory
{
    Validation,
    Conflict,
    Policy,
    Auth,
    Network,
    Protocol,
    Database,
    Resource,
    Cancelled,
    Internal
}
