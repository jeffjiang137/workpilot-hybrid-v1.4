using System.Text.Json.Nodes;

namespace WorkPilot.Domain.Automation;

/// <summary>Discriminated union of trigger kinds (spec §2). Stored as canonical JSON.</summary>
public enum TriggerType
{
    Manual,
    Once,
    Interval,
    CalendarDaily,
    CalendarWeekly,
    CalendarMonthly,
    DomainEvent
}

public sealed record TriggerDefinition(
    string TriggerId,
    TriggerType Type,
    bool Enabled,
    string? TimezoneId,
    DateTimeOffset? StartAtUtc,
    DateTimeOffset? EndAtUtc,
    long? IntervalSeconds,
    DateTimeOffset? AnchorAtUtc,
    string? LocalTime,
    int[]? DaysOfWeek,
    int? DayOfMonth,
    string? MissingDay,
    string? EventType,
    JsonArray? Filters)
{
    public JsonNode ToCanonicalJson()
    {
        var node = new JsonObject
        {
            ["trigger_id"] = TriggerId,
            ["type"] = Type.ToString(),
            ["enabled"] = Enabled,
            ["timezone_id"] = (JsonNode?)TimezoneId
        };
        if (StartAtUtc is { } s) node["start_at_utc"] = s.ToString("O");
        if (EndAtUtc is { } e) node["end_at_utc"] = e.ToString("O");
        if (IntervalSeconds is { } i) node["interval_seconds"] = i;
        if (AnchorAtUtc is { } a) node["anchor_at_utc"] = a.ToString("O");
        if (LocalTime is { } lt) node["local_time"] = lt;
        if (DaysOfWeek is { } dw) node["days_of_week"] = new JsonArray(dw.Select(x => (JsonNode)(long)x).ToArray());
        if (DayOfMonth is { } d) node["day_of_month"] = d;
        if (MissingDay is { } m) node["missing_day"] = m;
        if (EventType is { } ev) node["event_type"] = ev;
        if (Filters is { } f) node["filters"] = f.DeepClone();
        return node;
    }

    public static TriggerDefinition FromJson(JsonNode node)
    {
        var type = System.Enum.TryParse<TriggerType>(JsonParsing.GetString(node, "type"), true, out var t)
            ? t : TriggerType.Manual;
        return new TriggerDefinition(
            JsonParsing.GetString(node, "trigger_id") ?? string.Empty,
            type,
            JsonParsing.GetBool(node, "enabled", true),
            JsonParsing.GetString(node, "timezone_id"),
            ParseUtc(JsonParsing.GetString(node, "start_at_utc")),
            ParseUtc(JsonParsing.GetString(node, "end_at_utc")),
            JsonParsing.GetLong(node, "interval_seconds"),
            ParseUtc(JsonParsing.GetString(node, "anchor_at_utc")),
            JsonParsing.GetString(node, "local_time"),
            JsonParsing.GetIntArray(node, "days_of_week"),
            (int?)JsonParsing.GetLong(node, "day_of_month"),
            JsonParsing.GetString(node, "missing_day"),
            JsonParsing.GetString(node, "event_type"),
            JsonParsing.GetArray(node, "filters")?.DeepClone() as JsonArray);
    }

    private static DateTimeOffset? ParseUtc(string? s) =>
        string.IsNullOrEmpty(s) ? null : (DateTimeOffset.TryParse(s, out var v) ? v : null);
}
