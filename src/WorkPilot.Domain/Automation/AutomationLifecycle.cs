namespace WorkPilot.Domain.Automation;

/// <summary>Aggregate lifecycle. Storage strings are lowercase+underscore (DB CHECK constraint).</summary>
public enum AutomationLifecycle
{
    Draft,
    Enabled,
    Paused,
    PausedNeedsReview,
    Archived,
    Deleted
}
