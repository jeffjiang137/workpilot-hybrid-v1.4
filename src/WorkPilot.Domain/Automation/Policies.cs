namespace WorkPilot.Domain.Automation;

/// <summary>What to do when a scheduled run would overlap an in-flight run. Storage: lowercase+underscore.</summary>
public enum OverlapPolicy
{
    Skip,
    QueueOne,
    CancelPrevious
}

/// <summary>What to do about runs missed while paused/offline. Storage: lowercase+underscore.</summary>
public enum MissedRunPolicy
{
    Skip,
    RunOnce,
    CatchUp
}
