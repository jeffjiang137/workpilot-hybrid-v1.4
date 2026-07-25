namespace WorkPilot.Contracts.Primitives;

/// <summary>
/// Abstraction over the system clock. New business code MUST inject <see cref="IClock"/>
/// instead of calling <c>DateTimeOffset.UtcNow</c>/<c>Now</c> directly (AI dev rule §60).
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
    DateTimeOffset Now { get; }
}
