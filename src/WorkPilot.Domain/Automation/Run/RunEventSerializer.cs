using System.Collections.Generic;
using System.Globalization;
using System.Text;
using WorkPilot.Contracts.Primitives;

namespace WorkPilot.Domain.Automation.Run;

/// <summary>
/// Serializes run-event safe properties to <c>safe_properties_json</c> (doc 05 §3). Only allowlisted
/// keys (per <see cref="RunEventCatalog"/>) may be present; unknown keys are rejected (LOG-A02). Values
/// are coerced to safe JSON scalars with length/range validation. JSON is built manually — no DOM parser.
/// </summary>
public static class RunEventSerializer
{
    private static readonly HashSet<string> RiskValues = new(StringComparer.Ordinal)
    {
        "Low", "Medium", "High", "Critical"
    };

    /// <summary>Returns the safe-properties JSON, or a failure carrying <c>RUN_EVENT_CONTRACT_VIOLATION</c>.</summary>
    public static Result<string> Serialize(string kind, IReadOnlyDictionary<string, object?> properties)
    {
        if (!RunEventCatalog.TryGet(kind, out var descriptor))
            return Result<string>.Fail(RunErrors.LoggingContractViolationError(kind, "unknown_kind"));

        var sb = new StringBuilder();
        sb.Append('{');
        var first = true;
        foreach (var (key, raw) in properties)
        {
            if (!descriptor.Contains(key))
                return Result<string>.Fail(RunErrors.LoggingContractViolationError(kind, $"unknown_key:{key}"));
            if (!TryCoerce(descriptor.Get(key)!, key, raw, out var jsonToken, out var rejection))
                return Result<string>.Fail(RunErrors.LoggingContractViolationError(kind, rejection));

            if (!first) sb.Append(',');
            first = false;
            sb.Append('"').Append(EscapeKey(key)).Append("\":");
            sb.Append(jsonToken);
        }
        sb.Append('}');
        return Result<string>.Ok(sb.ToString());
    }

    private static bool TryCoerce(SafePropertySpec spec, string key, object? raw, out string json, out string rejection)
    {
        json = "";
        rejection = "";
        if (raw is null)
        {
            rejection = $"null_value:{key}";
            return false;
        }

        switch (spec.Type)
        {
            case SafePropertyType.Bool:
                if (raw is bool b) { json = b ? "true" : "false"; return true; }
                rejection = $"not_bool:{key}";
                return false;

            case SafePropertyType.Int:
            case SafePropertyType.Count:
            case SafePropertyType.ByteSize:
            case SafePropertyType.DurationMs:
            case SafePropertyType.DurationSeconds:
                if (TryToInt64(raw, out var n) && n >= spec.Min && (spec.Max == 0 || n <= spec.Max))
                {
                    json = n.ToString(CultureInfo.InvariantCulture);
                    return true;
                }
                rejection = $"bad_number:{key}";
                return false;

            default: // string-like: EnumString/StableId/Hash/ErrorCode/Risk
                if (raw is not string s)
                {
                    rejection = $"not_string:{key}";
                    return false;
                }
                if (spec.Type == SafePropertyType.Risk && !RiskValues.Contains(s))
                {
                    rejection = $"bad_risk:{key}";
                    return false;
                }
                var len = s.Length;
                if (len > spec.MaxLength || len > Limits.V1_5.MaxSafePropertyValueLength)
                {
                    rejection = $"too_long:{key}";
                    return false;
                }
                json = '"' + EscapeString(s) + '"';
                return true;
        }
    }

    private static bool TryToInt64(object raw, out long value)
    {
        value = 0;
        return raw switch
        {
            long l => (value = l) >= long.MinValue,
            int i => (value = i) >= long.MinValue,
            short sh => (value = sh) >= long.MinValue,
            byte by => (value = by) >= long.MinValue,
            string str => long.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out value),
            _ => false
        };
    }

    private static string EscapeKey(string key)
        => EscapeString(key);

    private static string EscapeString(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20)
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
