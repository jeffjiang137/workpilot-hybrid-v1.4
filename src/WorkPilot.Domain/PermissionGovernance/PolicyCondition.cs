using System.Text.Json;
using System.Text.Json.Serialization;
using WorkPilot.Contracts.Primitives;

namespace WorkPilot.Domain.PermissionGovernance;

/// <summary>
/// Allowed policy condition kinds (doc 07 §12). Only these are permitted; any unknown kind makes a
/// statement invalid (evaluator fails closed to Deny). Conditions are AND-combined; a single
/// statement may carry at most <see cref="Limits.V1_5.MaxPolicyConditionsPerStatement"/> conditions.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<PolicyConditionKind>))]
public enum PolicyConditionKind : int
{
    Unknown = 0,
    TimeWindow = 1,
    DaysOfWeek = 2,
    RunMode = 3,
    TriggerType = 4,
    TargetCountMax = 5,
    ResultSizeMax = 6,
    SourceHealthIn = 7
}

/// <summary>
/// A single policy condition. <see cref="DetailJson"/> holds kind-specific parameters as a JSON
/// object (e.g. <c>{"tz":"Asia/Shanghai","from":"09:00","to":"18:00"}</c> for TimeWindow). The
/// <see cref="PolicyConditionKind"/> enum itself is the allow-list; parsing of <see cref="DetailJson"/>
/// is performed by the T17 evaluator, which fails closed on any error.
/// </summary>
public sealed record PolicyCondition(PolicyConditionKind Kind, string DetailJson)
{
    /// <summary>True only for a known, allow-listed kind. Unknown conditions invalidate the statement.</summary>
    public bool IsValid() => Kind is not PolicyConditionKind.Unknown;

    public static PolicyCondition Parse(JsonElement element)
    {
        if (!element.TryGetProperty("kind", out var kindEl) || kindEl.ValueKind != JsonValueKind.String)
            return new PolicyCondition(PolicyConditionKind.Unknown, element.GetRawText());
        var kind = kindEl.GetString() switch
        {
            "time_window" => PolicyConditionKind.TimeWindow,
            "days_of_week" => PolicyConditionKind.DaysOfWeek,
            "run_mode" => PolicyConditionKind.RunMode,
            "trigger_type" => PolicyConditionKind.TriggerType,
            "target_count_max" => PolicyConditionKind.TargetCountMax,
            "result_size_max" => PolicyConditionKind.ResultSizeMax,
            "source_health_in" => PolicyConditionKind.SourceHealthIn,
            _ => PolicyConditionKind.Unknown
        };

        var detail = element.TryGetProperty("detail", out var detailEl)
            ? detailEl.GetRawText()
            : "{}";
        return new PolicyCondition(kind, detail);
    }

    public JsonElement ToJsonElement()
    {
        var obj = new Dictionary<string, object?>
        {
            ["kind"] = Kind.ToString().ToLowerInvariant() switch
            {
                "timewindow" => "time_window",
                "daysofweek" => "days_of_week",
                "runmode" => "run_mode",
                "triggertype" => "trigger_type",
                "targetcountmax" => "target_count_max",
                "resultsizemax" => "result_size_max",
                "sourcehealthin" => "source_health_in",
                _ => "unknown"
            },
            ["detail"] = JsonSerializer.Deserialize<JsonElement>(DetailJson)
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(obj);
        return JsonSerializer.Deserialize<JsonElement>(bytes);
    }
}
