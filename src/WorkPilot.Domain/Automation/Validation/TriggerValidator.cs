using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WorkPilot.Contracts.Primitives;

namespace WorkPilot.Domain.Automation.Validation;

/// <summary>
/// Pure validator for <see cref="TriggerDefinition"/> (AUT-003). Timezone *resolution* and next-time
/// computation live in <see cref="Scheduling.ScheduleCalculator"/>; this validator only checks
/// structural/value well-formedness so the same rules drive the editor, the enable Preflight, and
/// the materializer.
///
/// Convention: <see cref="TriggerDefinition.DaysOfWeek"/> stores <c>(int)System.DayOfWeek</c>
/// (0=Sunday … 6=Saturday); conversion to display day-names is a UI concern (T06).
/// </summary>
public static class TriggerValidator
{
    private static readonly Regex LocalTimePattern = new("^(?:[01][0-9]|2[0-3]):[0-5][0-9]$", RegexOptions.Compiled);
    private static readonly HashSet<string> AllowedEventTypes = new()
    {
        "task.created", "task.status_changed", "task.due_soon", "asset.index_completed",
        "project.changed", "connector.health_changed", "mcp.schema_changed"
    };
    private static readonly HashSet<string> AllowedFilterOps = new()
        { "eq", "ne", "in", "not_in", "exists", "starts_with" };
    private static readonly HashSet<string> AllowedMissingDay = new() { "skip", "last_day" };

    public static ValidationResult Validate(TriggerDefinition trigger)
    {
        var issues = new List<ValidationIssue>();
        var ptr = "/trigger";

        if (!Enum.IsDefined(typeof(TriggerType), trigger.Type))
            issues.Add(ValidationCodes.Error(ValidationCodes.TriggerTypeInvalid, $"{ptr}/type", KV("type", trigger.Type.ToString())));

        switch (trigger.Type)
        {
            case TriggerType.Interval:
                ValidateInterval(trigger, ptr, issues);
                break;
            case TriggerType.CalendarDaily:
            case TriggerType.CalendarWeekly:
                ValidateCalendarDays(trigger, ptr, issues);
                break;
            case TriggerType.CalendarMonthly:
                ValidateMonthly(trigger, ptr, issues);
                break;
            case TriggerType.Once:
                ValidateOnce(trigger, ptr, issues);
                break;
            case TriggerType.DomainEvent:
                ValidateDomainEvent(trigger, ptr, issues);
                break;
            case TriggerType.Manual:
                break;
        }

        return new ValidationResult(issues);
    }

    private static void ValidateInterval(TriggerDefinition t, string ptr, List<ValidationIssue> issues)
    {
        if (t.IntervalSeconds is not { } secs || secs < Limits.V1_5.MinIntervalSeconds || secs > Limits.V1_5.MaxIntervalSeconds)
            issues.Add(ValidationCodes.Error(ValidationCodes.IntervalSecondsInvalid, $"{ptr}/interval_seconds",
                KV("value", (t.IntervalSeconds ?? -1).ToString()), KV("min", Limits.V1_5.MinIntervalSeconds.ToString()), KV("max", Limits.V1_5.MaxIntervalSeconds.ToString())));
        if (t.AnchorAtUtc is null)
            issues.Add(ValidationCodes.Error(ValidationCodes.IntervalAnchorMissing, $"{ptr}/anchor_at_utc"));
    }

    private static void ValidateCalendarDays(TriggerDefinition t, string ptr, List<ValidationIssue> issues)
    {
        RequireTimezone(t, ptr, issues);
        RequireLocalTime(t, ptr, issues);
        if (t.DaysOfWeek is not { } days || days.Length < 1 || days.Length > Limits.V1_5.MaxDaysOfWeek || days.Distinct().Count() != days.Length)
            issues.Add(ValidationCodes.Error(ValidationCodes.CalendarDaysOfWeekInvalid, $"{ptr}/days_of_week",
                KV("count", t.DaysOfWeek?.Length.ToString() ?? "0")));
        else if (days.Any(d => d < 0 || d > 6))
            issues.Add(ValidationCodes.Error(ValidationCodes.CalendarDaysOfWeekInvalid, $"{ptr}/days_of_week", KV("value", string.Join(",", days))));
    }

    private static void ValidateMonthly(TriggerDefinition t, string ptr, List<ValidationIssue> issues)
    {
        RequireTimezone(t, ptr, issues);
        RequireLocalTime(t, ptr, issues);
        var validDay = (t.DayOfMonth is >= Limits.V1_5.MinDayOfMonth and <= Limits.V1_5.MaxDayOfMonth)
                       || t.DayOfMonth is null && t.MissingDay == "last_day";
        // DayOfMonth is nullable int; "last" is represented by MissingDay=="last_day" with DayOfMonth null.
        if (t.DayOfMonth is { } d && (d < Limits.V1_5.MinDayOfMonth || d > Limits.V1_5.MaxDayOfMonth))
            validDay = false;
        if (t.DayOfMonth is null && t.MissingDay != "last_day")
            validDay = false;
        if (!validDay)
            issues.Add(ValidationCodes.Error(ValidationCodes.MonthlyDayInvalid, $"{ptr}/day_of_month",
                KV("day_of_month", t.DayOfMonth?.ToString() ?? "null"), KV("missing_day", t.MissingDay ?? "null")));
        if (t.MissingDay is not null && !AllowedMissingDay.Contains(t.MissingDay))
            issues.Add(ValidationCodes.Error(ValidationCodes.MonthlyMissingDayInvalid, $"{ptr}/missing_day", KV("value", t.MissingDay)));
    }

    private static void ValidateOnce(TriggerDefinition t, string ptr, List<ValidationIssue> issues)
    {
        RequireTimezone(t, ptr, issues);
        if (t.StartAtUtc is null)
            issues.Add(ValidationCodes.Error(ValidationCodes.OnceFieldsMissing, $"{ptr}/start_at_utc"));
    }

    private static void ValidateDomainEvent(TriggerDefinition t, string ptr, List<ValidationIssue> issues)
    {
        if (t.EventType is null || !AllowedEventTypes.Contains(t.EventType))
            issues.Add(ValidationCodes.Error(ValidationCodes.DomainEventTypeInvalid, $"{ptr}/event_type", KV("value", t.EventType ?? "null")));
        if (t.Filters is null || t.Filters.Count == 0)
        {
            issues.Add(ValidationCodes.Error(ValidationCodes.DomainEventFiltersInvalid, $"{ptr}/filters", KV("reason", "empty")));
            return;
        }
        if (t.Filters.Count > Limits.V1_5.MaxDomainEventFilters)
            issues.Add(ValidationCodes.Error(ValidationCodes.DomainEventFiltersInvalid, $"{ptr}/filters",
                KV("count", t.Filters.Count.ToString()), KV("max", Limits.V1_5.MaxDomainEventFilters.ToString())));
        for (var i = 0; i < t.Filters.Count; i++)
        {
            var f = t.Filters[i];
            if (f is null) continue;
            var fptr = $"{ptr}/filters/{i}";
            var field = JsonParsing.GetString(f, "field");
            var op = JsonParsing.GetString(f, "op");
            if (string.IsNullOrEmpty(field) || !NodeIdPattern().IsMatch(field))
                issues.Add(ValidationCodes.Error(ValidationCodes.DomainEventFiltersInvalid, $"{fptr}/field", KV("value", field ?? "null")));
            if (op is null || !AllowedFilterOps.Contains(op))
                issues.Add(ValidationCodes.Error(ValidationCodes.DomainEventFiltersInvalid, $"{fptr}/op", KV("value", op ?? "null")));
            if (f["value"] is { } v && v.GetValueKind() == System.Text.Json.JsonValueKind.String)
            {
                var sv = v.GetValue<string>();
                if (sv.Length > Limits.V1_5.MaxFilterValueLength)
                    issues.Add(ValidationCodes.Error(ValidationCodes.DomainEventFiltersInvalid, $"{fptr}/value",
                        KV("len", sv.Length.ToString()), KV("max", Limits.V1_5.MaxFilterValueLength.ToString())));
            }
        }
    }

    private static void RequireTimezone(TriggerDefinition t, string ptr, List<ValidationIssue> issues)
    {
        if (string.IsNullOrEmpty(t.TimezoneId))
            issues.Add(ValidationCodes.Error(ValidationCodes.CalendarLocalTimeInvalid, $"{ptr}/timezone_id", KV("reason", "missing")));
    }

    private static void RequireLocalTime(TriggerDefinition t, string ptr, List<ValidationIssue> issues)
    {
        var lt = t.LocalTime;
        if (string.IsNullOrEmpty(lt) || !LocalTimePattern.IsMatch(lt))
            issues.Add(ValidationCodes.Error(ValidationCodes.CalendarLocalTimeInvalid, $"{ptr}/local_time", KV("value", lt ?? "null")));
    }

    private static Regex NodeIdPattern() => new("^[a-z][a-z0-9_]{0,31}$", RegexOptions.Compiled);

    private static (string Key, string Value) KV(string k, string v) => (k, v);
}
