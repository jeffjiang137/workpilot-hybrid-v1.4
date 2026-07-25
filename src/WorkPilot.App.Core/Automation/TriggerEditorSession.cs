using System.Text.Json.Nodes;
using WorkPilot.App.Core.Primitives;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Domain.Automation;
using WorkPilot.Domain.Automation.Validation;

namespace WorkPilot.App.Core.Automation;

/// <summary>
/// Editable wrapper around an immutable <see cref="TriggerDefinition"/>. Every mutation rebuilds the
/// underlying record (records are immutable) and re-runs the shared T05 <see cref="TriggerValidator"/>
/// so the preview/preflight always reflect the current trigger. No I/O, no clock, no static state.
/// </summary>
public sealed class TriggerEditorSession : ObservableBase
{
    public const string MissingDaySkip = "skip";
    public const string MissingDayLastDay = "last_day";

    private TriggerDefinition _trigger;

    public TriggerEditorSession(TriggerDefinition initial)
    {
        _trigger = initial ?? throw new System.ArgumentNullException(nameof(initial));
    }

    /// <summary>The current immutable trigger. Replaces the working copy on every edit.</summary>
    public TriggerDefinition Trigger
    {
        get => _trigger;
        private set
        {
            if (Set(ref _trigger, value))
                Raise(nameof(Validation));
        }
    }

    /// <summary>Live validation using the single T05 validator (shared by preview and materializer).</summary>
    public ValidationResult Validation => TriggerValidator.Validate(_trigger);

    public TriggerType Type => _trigger.Type;
    public bool Enabled { get => _trigger.Enabled; set => Update(t => t with { Enabled = value }); }
    public string? TimezoneId { get => _trigger.TimezoneId; set => Update(t => t with { TimezoneId = value }); }
    public long? IntervalSeconds { get => _trigger.IntervalSeconds; set => Update(t => t with { IntervalSeconds = value }); }
    public DateTimeOffset? AnchorAtUtc { get => _trigger.AnchorAtUtc; set => Update(t => t with { AnchorAtUtc = value }); }
    public string? LocalTime { get => _trigger.LocalTime; set => Update(t => t with { LocalTime = value }); }
    public int[]? DaysOfWeek { get => _trigger.DaysOfWeek; set => Update(t => t with { DaysOfWeek = value }); }
    public int? DayOfMonth { get => _trigger.DayOfMonth; set => Update(t => t with { DayOfMonth = value }); }
    public string? MissingDay { get => _trigger.MissingDay; set => Update(t => t with { MissingDay = value }); }
    public DateTimeOffset? StartAtUtc { get => _trigger.StartAtUtc; set => Update(t => t with { StartAtUtc = value }); }
    public DateTimeOffset? EndAtUtc { get => _trigger.EndAtUtc; set => Update(t => t with { EndAtUtc = value }); }
    public string? EventType { get => _trigger.EventType; set => Update(t => t with { EventType = value }); }
    public JsonArray? Filters { get => _trigger.Filters; set => Update(t => t with { Filters = value }); }

    private void Update(System.Func<TriggerDefinition, TriggerDefinition> mutate) => Trigger = mutate(_trigger);

    /// <summary>
    /// Switches the trigger kind, preserving identity/enable/window fields and seeding type-specific
    /// defaults so the validator never sees an inconsistent intermediate state. <paramref name="now"/>
    /// (injected clock) seeds the interval anchor; pass null to leave it unset.
    /// </summary>
    public void ChangeType(TriggerType type, DateTimeOffset? now = null)
    {
        var next = type switch
        {
            TriggerType.Manual => new TriggerDefinition(_trigger.TriggerId, type, _trigger.Enabled, null, null, null, null, null, null, null, null, null, null, null),
            TriggerType.Once => new TriggerDefinition(_trigger.TriggerId, type, _trigger.Enabled, _trigger.TimezoneId, null, null, null, null, null, null, null, null, null, null),
            TriggerType.Interval => new TriggerDefinition(_trigger.TriggerId, type, _trigger.Enabled, _trigger.TimezoneId, null, null,
                Limits.V1_5.MinIntervalSeconds, now, null, null, null, null, null, null),
            TriggerType.CalendarDaily => new TriggerDefinition(_trigger.TriggerId, type, _trigger.Enabled, _trigger.TimezoneId, null, null, null, null, "09:00", new[] { 1, 2, 3, 4, 5 }, null, null, null, null),
            TriggerType.CalendarWeekly => new TriggerDefinition(_trigger.TriggerId, type, _trigger.Enabled, _trigger.TimezoneId, null, null, null, null, "09:00", new[] { 1 }, null, null, null, null),
            TriggerType.CalendarMonthly => new TriggerDefinition(_trigger.TriggerId, type, _trigger.Enabled, _trigger.TimezoneId, null, null, null, null, "09:00", null, 1, MissingDaySkip, null, null),
            TriggerType.DomainEvent => new TriggerDefinition(_trigger.TriggerId, type, _trigger.Enabled, null, null, null, null, null, null, null, null, null, "file.created", null),
            _ => _trigger
        };
        Trigger = next;
    }
}
