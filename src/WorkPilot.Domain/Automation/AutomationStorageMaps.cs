namespace WorkPilot.Domain.Automation;

/// <summary>
/// Explicit mapping between the C# (PascalCase) automation enums and their lowercase+underscore
/// storage strings, which the v1.5 schema enforces via CHECK constraints. Keeping the mapping in
/// one place avoids ad-hoc <c>ToString()</c>/<c>Enum.Parse</c> that would violate those constraints.
/// </summary>
public static class AutomationStorageMaps
{
    public static string ToStorage(this AutomationLifecycle l) => l switch
    {
        AutomationLifecycle.Draft => "draft",
        AutomationLifecycle.Enabled => "enabled",
        AutomationLifecycle.Paused => "paused",
        AutomationLifecycle.PausedNeedsReview => "paused_needs_review",
        AutomationLifecycle.Archived => "archived",
        AutomationLifecycle.Deleted => "deleted",
        _ => throw new System.ArgumentOutOfRangeException(nameof(l), l, "Unknown lifecycle")
    };

    public static AutomationLifecycle LifecycleFromStorage(string s) => s switch
    {
        "draft" => AutomationLifecycle.Draft,
        "enabled" => AutomationLifecycle.Enabled,
        "paused" => AutomationLifecycle.Paused,
        "paused_needs_review" => AutomationLifecycle.PausedNeedsReview,
        "archived" => AutomationLifecycle.Archived,
        "deleted" => AutomationLifecycle.Deleted,
        _ => throw new System.ArgumentOutOfRangeException(nameof(s), s, "Unknown lifecycle storage value")
    };

    public static string ToStorage(this OverlapPolicy p) => p switch
    {
        OverlapPolicy.Skip => "skip",
        OverlapPolicy.QueueOne => "queue_one",
        OverlapPolicy.CancelPrevious => "cancel_previous",
        _ => throw new System.ArgumentOutOfRangeException(nameof(p), p, "Unknown overlap policy")
    };

    public static OverlapPolicy OverlapFromStorage(string s) => s switch
    {
        "skip" => OverlapPolicy.Skip,
        "queue_one" => OverlapPolicy.QueueOne,
        "cancel_previous" => OverlapPolicy.CancelPrevious,
        _ => throw new System.ArgumentOutOfRangeException(nameof(s), s, "Unknown overlap policy storage value")
    };

    public static string ToStorage(this MissedRunPolicy p) => p switch
    {
        MissedRunPolicy.Skip => "skip",
        MissedRunPolicy.RunOnce => "run_once",
        MissedRunPolicy.CatchUp => "catch_up",
        _ => throw new System.ArgumentOutOfRangeException(nameof(p), p, "Unknown missed-run policy")
    };

    public static MissedRunPolicy MissedFromStorage(string s) => s switch
    {
        "skip" => MissedRunPolicy.Skip,
        "run_once" => MissedRunPolicy.RunOnce,
        "catch_up" => MissedRunPolicy.CatchUp,
        _ => throw new System.ArgumentOutOfRangeException(nameof(s), s, "Unknown missed-run policy storage value")
    };

    public static string ToStorage(this TriggerType t) => t switch
    {
        TriggerType.Manual => "manual",
        TriggerType.Once => "once",
        TriggerType.Interval => "interval",
        TriggerType.CalendarDaily => "calendar_daily",
        TriggerType.CalendarWeekly => "calendar_weekly",
        TriggerType.CalendarMonthly => "calendar_monthly",
        TriggerType.DomainEvent => "domain_event",
        _ => throw new System.ArgumentOutOfRangeException(nameof(t), t, "Unknown trigger type")
    };
}
