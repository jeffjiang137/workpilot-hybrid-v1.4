using System.Globalization;
using System.Text;
using WorkPilot.Contracts.Primitives;

namespace WorkPilot.Domain.PermissionGovernance;

/// <summary>
/// Produces the canonical, deterministic JSON of a policy version's statements and its SHA-256
/// fingerprint (PER-009 / doc 07 §13). The canonical form is computed with
/// <see cref="JcsCanonicalizer"/> (RFC 8785-style, defined in Contracts so Domain never touches the
/// JSON-DOM API). Same statements + same version always yield a byte-identical canonical string and
/// hash, which is what <c>policy_versions.canonical_sha256</c> stores and SEC-106 verifies.
/// </summary>
public static class PolicyCanonicalizer
{
    /// <summary>Assembles a deterministic JSON array of the statements (sorted by id) and returns its
    /// JCS-canonical form. This is what <c>policy_versions.document_json</c> stores.</summary>
    public static string CanonicalizeStatements(IEnumerable<PolicyStatement> statements)
    {
        var ordered = statements.OrderBy(s => s.Id.Value, StringComparer.Ordinal).ToList();
        var builder = new StringBuilder();
        builder.Append('[');
        for (var i = 0; i < ordered.Count; i++)
        {
            if (i > 0) builder.Append(',');
            AppendStatement(builder, ordered[i]);
        }

        builder.Append(']');
        return JcsCanonicalizer.Canonicalize(builder.ToString());
    }

    /// <summary>SHA-256 (lowercase hex) of <see cref="CanonicalizeStatements"/>.</summary>
    public static string HashStatements(IEnumerable<PolicyStatement> statements)
        => JcsCanonicalizer.CanonicalizeToSha256(CanonicalizeStatements(statements));

    private static void AppendStatement(StringBuilder builder, PolicyStatement s)
    {
        builder.Append('{');
        builder.Append("\"id\":").Append(Quote(s.Id.Value)).Append(',');
        builder.Append("\"enabled\":").Append(s.Enabled ? "true" : "false").Append(',');
        builder.Append("\"effect\":").Append(Quote(s.Effect.ToString().ToLowerInvariant())).Append(',');
        builder.Append("\"subjects\":[");
        for (var i = 0; i < s.Subjects.Count; i++)
        {
            if (i > 0) builder.Append(',');
            builder.Append(Quote(s.Subjects[i].ToString().ToLowerInvariant()));
        }

        builder.Append("],");
        builder.Append("\"source_selector\":").Append(s.SourceSelectorJson).Append(',');
        builder.Append("\"capability_selector\":").Append(s.CapabilitySelectorJson).Append(',');
        builder.Append("\"risk_min\":").Append(((int)s.RiskMin).ToString(CultureInfo.InvariantCulture)).Append(',');
        builder.Append("\"risk_max\":").Append(((int)s.RiskMax).ToString(CultureInfo.InvariantCulture)).Append(',');
        builder.Append("\"resource_scope\":").Append(s.Scope is null ? "null" : s.Scope.ToStorageJson()).Append(',');
        builder.Append("\"conditions\":[");
        for (var i = 0; i < s.Conditions.Count; i++)
        {
            if (i > 0) builder.Append(',');
            AppendCondition(builder, s.Conditions[i]);
        }

        builder.Append("],");
        builder.Append("\"priority\":").Append(s.Priority.ToString(CultureInfo.InvariantCulture));
        builder.Append('}');
    }

    private static void AppendCondition(StringBuilder builder, PolicyCondition c)
    {
        var kindText = c.Kind switch
        {
            PolicyConditionKind.TimeWindow => "time_window",
            PolicyConditionKind.DaysOfWeek => "days_of_week",
            PolicyConditionKind.RunMode => "run_mode",
            PolicyConditionKind.TriggerType => "trigger_type",
            PolicyConditionKind.TargetCountMax => "target_count_max",
            PolicyConditionKind.ResultSizeMax => "result_size_max",
            PolicyConditionKind.SourceHealthIn => "source_health_in",
            _ => "unknown"
        };
        builder.Append('{');
        builder.Append("\"kind\":").Append(Quote(kindText)).Append(',');
        builder.Append("\"detail\":").Append(c.DetailJson);
        builder.Append('}');
    }

    private static string Quote(string value)
        => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
