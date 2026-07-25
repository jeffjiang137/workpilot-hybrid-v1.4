using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace WorkPilot.Domain.PermissionGovernance.Evaluation;

/// <summary>
/// Evaluates the allow-listed policy conditions (doc 07 §12) against the runtime
/// <see cref="EvaluationContext"/>. Conditions are AND-combined; an unknown kind or malformed detail
/// is a <see cref="ConditionMatch.ParseError"/> which the evaluator turns into Deny (fail closed).
/// Non-matching but valid conditions simply make the owning statement non-applicable. Temporal
/// conditions (time_window / days_of_week) that are currently unmet are reported so the evaluator can
/// choose Defer instead of dropping the statement.
/// </summary>
public static class ConditionEvaluator
{
    public enum ConditionMatch { Matched, NotMatched, ParseError }

    public static bool IsTemporal(PolicyConditionKind kind)
        => kind is PolicyConditionKind.TimeWindow or PolicyConditionKind.DaysOfWeek;

    /// <summary>
    /// Evaluates all conditions. Returns <see cref="ConditionMatch.ParseError"/> on the first
    /// unparseable condition; otherwise <see cref="ConditionMatch.NotMatched"/> if any condition is
    /// unsatisfied (setting <paramref name="temporalUnmet"/> when the unsatisfied one is temporal);
    /// else <see cref="ConditionMatch.Matched"/>.
    /// </summary>
    public static ConditionMatch EvaluateAll(
        IReadOnlyList<PolicyCondition> conditions, EvaluationContext ctx, out bool temporalUnmet)
    {
        temporalUnmet = false;
        foreach (var c in conditions)
        {
            var r = Evaluate(c, ctx);
            if (r == ConditionMatch.ParseError)
                return ConditionMatch.ParseError;
            if (r == ConditionMatch.NotMatched)
            {
                if (IsTemporal(c.Kind))
                    temporalUnmet = true;
                return ConditionMatch.NotMatched;
            }
        }
        return ConditionMatch.Matched;
    }

    private static ConditionMatch Evaluate(PolicyCondition c, EvaluationContext ctx)
    {
        if (c.Kind == PolicyConditionKind.Unknown)
            return ConditionMatch.ParseError;
        if (string.IsNullOrWhiteSpace(c.DetailJson))
            return ConditionMatch.Matched; // no detail → trivially satisfied

        JsonElement detail;
        try
        {
            using var doc = JsonDocument.Parse(c.DetailJson);
            detail = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return ConditionMatch.ParseError;
        }

        try
        {
            return c.Kind switch
            {
                PolicyConditionKind.TimeWindow => EvalTimeWindow(detail, ctx),
                PolicyConditionKind.DaysOfWeek => EvalDaysOfWeek(detail, ctx),
                PolicyConditionKind.RunMode => EvalInSet(detail, "modes", ctx.RunMode),
                PolicyConditionKind.TriggerType => EvalInSet(detail, "types", ctx.TriggerType),
                PolicyConditionKind.TargetCountMax => EvalMax(detail, "max", ctx.TargetCount),
                PolicyConditionKind.ResultSizeMax => EvalMax(detail, "max", ctx.ResultSize),
                PolicyConditionKind.SourceHealthIn => EvalInSet(detail, "states", ctx.SourceHealth),
                _ => ConditionMatch.ParseError
            };
        }
        catch (Exception) when (IsBenignParseFailure())
        {
            return ConditionMatch.ParseError;
        }
    }

    private static bool IsBenignParseFailure() => true;

    private static ConditionMatch EvalTimeWindow(JsonElement detail, EvaluationContext ctx)
    {
        var fromStr = detail.TryGetProperty("from", out var f) ? f.GetString() : null;
        var toStr = detail.TryGetProperty("to", out var t) ? t.GetString() : null;
        if (fromStr is null || toStr is null) return ConditionMatch.ParseError;
        if (!TimeSpan.TryParse(fromStr, CultureInfo.InvariantCulture, out var from)
            || !TimeSpan.TryParse(toStr, CultureInfo.InvariantCulture, out var to))
            return ConditionMatch.ParseError;

        var tz = ResolveZone(detail);
        var local = TimeZoneInfo.ConvertTime(ctx.NowUtc, tz).TimeOfDay;
        return (from <= local && local <= to) ? ConditionMatch.Matched : ConditionMatch.NotMatched;
    }

    private static ConditionMatch EvalDaysOfWeek(JsonElement detail, EvaluationContext ctx)
    {
        if (!detail.TryGetProperty("days", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return ConditionMatch.ParseError;
        // ISO weekday: Monday=1 .. Sunday=7
        var iso = ((int)ctx.NowUtc.DayOfWeek + 6) % 7 + 1;
        foreach (var e in arr.EnumerateArray())
            if (e.TryGetInt32(out var d) && d == iso)
                return ConditionMatch.Matched;
        return ConditionMatch.NotMatched;
    }

    private static ConditionMatch EvalInSet(JsonElement detail, string prop, string value)
    {
        if (!detail.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return ConditionMatch.ParseError;
        foreach (var e in arr.EnumerateArray())
            if (e.ValueKind == JsonValueKind.String && string.Equals(e.GetString(), value, StringComparison.Ordinal))
                return ConditionMatch.Matched;
        return ConditionMatch.NotMatched;
    }

    private static ConditionMatch EvalMax(JsonElement detail, string prop, long actual)
    {
        if (!detail.TryGetProperty(prop, out var m) || !m.TryGetInt64(out var max))
            return ConditionMatch.ParseError;
        return actual <= max ? ConditionMatch.Matched : ConditionMatch.NotMatched;
    }

    private static TimeZoneInfo ResolveZone(JsonElement detail)
    {
        if (detail.TryGetProperty("tz", out var tzEl) && tzEl.ValueKind == JsonValueKind.String)
        {
            var id = tzEl.GetString()!;
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch { /* fall through to UTC */ }
        }
        return TimeZoneInfo.Utc;
    }
}
