using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation;

namespace WorkPilot.Application.Automation.Definition;

/// <summary>
/// Validates a definition export envelope against the portable contract (AUT-006 / AUT-A07 / AUT-A08).
/// It is intentionally pragmatic: the v1.5 internal model does not yet carry every field the idealised
/// <c>automation-definition.schema.json</c> describes (node-level <c>retry_policy</c>, capability
/// <c>resource_scope</c>/<c>idempotency_mode</c>/<c>output_key</c>, total-token budget), so the
/// validator enforces the invariants that matter — required structure, enum/pattern/ranges, and a
/// hard ban on any secret / grant / run-id key anywhere in the document — and surfaces unresolved
/// source/timezone as non-blocking warnings. See the T22 report for the full deviation list.
/// </summary>
public sealed class DefinitionSchemaValidator
{
    private static readonly Regex Hex32 = new("^[0-9a-f]{32}$", RegexOptions.Compiled);
    private static readonly Regex Hex64 = new("^[0-9a-f]{64}$", RegexOptions.Compiled);
    // AUT-A07: an export must never carry a credential, grant or run identifier.
    private static readonly HashSet<string> ForbiddenKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "secret", "secrets", "password", "passwd", "pwd", "token", "api_key", "apikey",
        "access_key", "private_key", "credential", "credentials", "authorization", "auth",
        "grant", "grant_id", "run_id", "runid", "receipt", "receipt_id"
    };

    private static readonly HashSet<string> TriggerTypes = new()
    {
        "manual", "once", "interval", "calendar_daily", "calendar_weekly",
        "calendar_monthly", "domain_event"
    };

    private static readonly HashSet<string> OverlapPolicies = new() { "skip", "queue_one", "cancel_previous" };
    private static readonly HashSet<string> MissedPolicies = new() { "skip", "run_once", "catch_up" };
    private static readonly HashSet<string> NodeKinds = new()
    {
        "agent_prompt", "capability_call", "condition", "delay", "notification"
    };

    private static readonly HashSet<string> SourceKinds = new() { "builtin", "connector", "mcp" };

    public Result<ParsedDefinition> Validate(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Result<ParsedDefinition>.Fail(AutomationErrors.DefinitionMalformedError("empty"));

        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException ex) { return Result<ParsedDefinition>.Fail(AutomationErrors.DefinitionMalformedError("json:" + ex.Message)); }
        if (root is not JsonObject env)
            return Result<ParsedDefinition>.Fail(AutomationErrors.DefinitionMalformedError("root_not_object"));

        var forbidden = ScanForbiddenKeys(env, "$");
        if (forbidden is not null)
            return Result<ParsedDefinition>.Fail(AutomationErrors.DefinitionContainsSecretError(forbidden));

        // schema_version must be 1.
        var sv = env["schema_version"];
        if (sv is not JsonValue || sv.GetValueKind() != JsonValueKind.Number || sv.GetValue<int>() != 1)
            return Result<ParsedDefinition>.Fail(AutomationErrors.DefinitionInvalidSchemaVersionError(sv is JsonValue ? sv.GetValue<int>() : 0));

        var name = AsString(env, "name");
        if (string.IsNullOrWhiteSpace(name) || name.Length > Limits.V1_5.MaxAutomationNameLength)
            return Result<ParsedDefinition>.Fail(AutomationErrors.DefinitionMalformedError("name"));
        var description = AsString(env, "description") ?? string.Empty;
        if (description.Length > Limits.V1_5.MaxAutomationDescriptionLength)
            return Result<ParsedDefinition>.Fail(AutomationErrors.DefinitionMalformedError("description"));

        var warnings = new List<ImportWarning>();

        var binding = ValidateBinding(env["binding"], warnings);
        if (binding is null) return Result<ParsedDefinition>.Fail(AutomationErrors.DefinitionInvalidBindingError("missing_or_invalid"));

        var trigger = ValidateTrigger(env["trigger"], warnings);
        if (trigger is null) return Result<ParsedDefinition>.Fail(AutomationErrors.DefinitionInvalidTriggerError("missing_or_invalid"));

        var workflow = ValidateWorkflow(env["workflow"], warnings);
        if (workflow is null) return Result<ParsedDefinition>.Fail(AutomationErrors.DefinitionInvalidWorkflowError("missing_or_invalid"));

        var budget = ValidateBudget(env["budget"]);
        if (budget is null) return Result<ParsedDefinition>.Fail(AutomationErrors.DefinitionInvalidBudgetError("missing_or_invalid"));

        var permission = ValidatePermission(env["permission_request"]);
        if (permission is null) return Result<ParsedDefinition>.Fail(AutomationErrors.DefinitionInvalidPermissionError("missing_or_invalid"));

        var overlap = AsEnum<OverlapPolicy>(env, "overlap_policy", OverlapPolicies);
        if (overlap is null) return Result<ParsedDefinition>.Fail(AutomationErrors.DefinitionInvalidBindingError("overlap_policy"));
        var missed = AsEnum<MissedRunPolicy>(env, "missed_run_policy", MissedPolicies);
        if (missed is null) return Result<ParsedDefinition>.Fail(AutomationErrors.DefinitionInvalidBindingError("missed_run_policy"));

        var parsed = new ParsedDefinition
        {
            Name = name!,
            Description = description,
            SpaceId = binding.Value.SpaceId,
            ProjectId = binding.Value.ProjectId,
            ExpertId = binding.Value.ExpertId,
            Trigger = trigger,
            Workflow = workflow,
            Budget = budget,
            OverlapPolicy = overlap.Value,
            MissedRunPolicy = missed.Value,
            PermissionRequest = permission,
            Warnings = warnings
        };
        return Result<ParsedDefinition>.Ok(parsed);
    }

    // ---- binding ----------------------------------------------------------

    private (SpaceId SpaceId, string? ProjectId, string? ExpertId)? ValidateBinding(JsonNode? node, List<ImportWarning> warnings)
    {
        if (node is not JsonObject b) return null;
        var space = AsString(b, "space_id");
        if (space is null || !Hex32.IsMatch(space)) return null;
        var expert = AsString(b, "expert_id");
        if (string.IsNullOrWhiteSpace(expert))
        {
            warnings.Add(new ImportWarning(ImportWarningKind.UnresolvedSource, "Definition.Import.UnresolvedSource", "expert_id"));
        }
        else if (!Hex32.IsMatch(expert))
        {
            return null; // malformed expert id is a hard error, not a warning
        }
        var project = AsString(b, "project_id");
        if (project is not null && !Hex32.IsMatch(project))
            return null;
        if (project is null)
            warnings.Add(new ImportWarning(ImportWarningKind.MissingProject, "Definition.Import.MissingProject", "project_id"));
        var modelPolicy = AsString(b, "model_policy");
        if (modelPolicy is not null && modelPolicy != "expert_revision")
            return null;
        return (SpaceId.Parse(space), project, expert);
    }

    // ---- trigger ----------------------------------------------------------

    private TriggerDefinition? ValidateTrigger(JsonNode? node, List<ImportWarning> warnings)
    {
        if (node is not JsonObject t) return null;
        var triggerId = AsString(t, "trigger_id");
        if (string.IsNullOrWhiteSpace(triggerId)) return null;
        var typeStr = AsString(t, "type");
        if (typeStr is null || !TriggerTypes.Contains(typeStr)) return null;
        var type = typeStr switch
        {
            "manual" => TriggerType.Manual,
            "once" => TriggerType.Once,
            "interval" => TriggerType.Interval,
            "calendar_daily" => TriggerType.CalendarDaily,
            "calendar_weekly" => TriggerType.CalendarWeekly,
            "calendar_monthly" => TriggerType.CalendarMonthly,
            _ => TriggerType.DomainEvent
        };
        var enabled = GetBool(t, "enabled", true);
        var timezone = AsString(t, "timezone_id");
        var needsTz = type is TriggerType.Once or TriggerType.CalendarDaily or TriggerType.CalendarWeekly or TriggerType.CalendarMonthly;
        if (needsTz && string.IsNullOrWhiteSpace(timezone))
            warnings.Add(new ImportWarning(ImportWarningKind.UnresolvedTimezone, "Definition.Import.UnresolvedTimezone", typeStr));

        var interval = GetLong(t, "interval_seconds");
        var anchor = ParseUtc(AsString(t, "anchor_at_utc"));
        var localTime = AsString(t, "local_time");
        var days = GetIntArray(t, "days_of_week");
        var dayOfMonth = GetLong(t, "day_of_month");
        var missingDay = AsString(t, "missing_day");
        var eventType = AsString(t, "event_type");
        var filters = t["filters"] as JsonArray;

        switch (type)
        {
            case TriggerType.Interval:
                if (interval is not { } iv || iv < Limits.V1_5.MinIntervalSeconds || iv > Limits.V1_5.MaxIntervalSeconds) return null;
                if (anchor is null) return null;
                return new TriggerDefinition(triggerId, type, enabled, null, null, null, iv, anchor, null, null, null, null, null, null);
            case TriggerType.Once:
                if (localTime is null) return null;
                var onceAnchor = CombineLocal(AsString(t, "local_date"), localTime);
                if (onceAnchor is null) return null;
                return new TriggerDefinition(triggerId, type, enabled, timezone, null, null, null, onceAnchor, localTime, null, null, null, null, null);
            case TriggerType.CalendarDaily:
            case TriggerType.CalendarWeekly:
                if (localTime is null) return null;
                if (type == TriggerType.CalendarWeekly && (days is null || days.Length == 0)) return null;
                return new TriggerDefinition(triggerId, type, enabled, timezone, null, null, null, null, localTime, days, null, missingDay, null, null);
            case TriggerType.CalendarMonthly:
                if (localTime is null) return null;
                if (dayOfMonth is null && missingDay != "last_day") return null;
                return new TriggerDefinition(triggerId, type, enabled, timezone, null, null, null, null, localTime, days, (int?)dayOfMonth, missingDay, null, null);
            case TriggerType.DomainEvent:
                if (eventType is null || filters is null) return null;
                return new TriggerDefinition(triggerId, type, enabled, timezone, null, null, null, null, null, null, null, null, eventType, (JsonArray)filters.DeepClone());
            default: // Manual
                return new TriggerDefinition(triggerId, type, enabled, null, null, null, null, null, null, null, null, null, null, null);
        }
    }

    // ---- workflow ---------------------------------------------------------

    private WorkflowDefinition? ValidateWorkflow(JsonNode? node, List<ImportWarning> warnings)
    {
        if (node is not JsonObject w) return null;
        if (w["schema_version"] is not JsonValue || w["schema_version"]!.GetValue<int>() != 1) return null;
        var entry = AsString(w, "entry_node_id");
        if (string.IsNullOrWhiteSpace(entry)) return null;
        if (w["nodes"] is not JsonArray nodes) return null;
        if (nodes.Count < 1 || nodes.Count > Limits.V1_5.MaxWorkflowNodes) return null;
        if (w["edges"] is JsonArray edges && edges.Count > Limits.V1_5.MaxWorkflowEdges) return null;

        var builtNodes = new List<WorkflowNode>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in nodes)
        {
            if (n is not JsonObject no) return null;
            var id = AsString(no, "node_id");
            if (string.IsNullOrWhiteSpace(id) || !ids.Add(id)) return null;
            var kind = AsString(no, "kind");
            if (kind is null || !NodeKinds.Contains(kind)) return null;
            var display = AsString(no, "display_name") ?? string.Empty;
            var timeout = GetLong(no, "timeout_seconds");
            if (timeout is null || timeout < Limits.V1_5.MinWorkflowNodeTimeoutSeconds || timeout > Limits.V1_5.MaxWorkflowNodeTimeoutSeconds) return null;
            var disabled = GetBool(no, "disabled", false);
            var payload = no["payload"] as JsonObject;
            builtNodes.Add(new WorkflowNode(id, display, kind, (int)timeout!.Value, disabled, payload?.DeepClone() as JsonObject));
        }

        var builtEdges = new List<WorkflowEdge>();
        if (w["edges"] is JsonArray eArr)
        {
            foreach (var e in eArr)
            {
                if (e is not JsonObject eo) return null;
                var from = AsString(eo, "from_node_id");
                var to = AsString(eo, "to_node_id");
                var branch = AsString(eo, "branch") ?? "next";
                if (from is null || to is null || !ids.Contains(from) || !ids.Contains(to)) return null;
                builtEdges.Add(new WorkflowEdge(from, to, branch));
            }
        }

        if (!ids.Contains(entry)) return null;
        return new WorkflowDefinition(1, entry, builtNodes, builtEdges);
    }

    // ---- budget -----------------------------------------------------------

    private RunBudget? ValidateBudget(JsonNode? node)
    {
        if (node is not JsonObject b) return null;
        var wall = GetLong(b, "wall_clock_seconds");
        var turns = GetLong(b, "model_turns");
        var caps = GetLong(b, "capability_calls");
        var bytes = GetLong(b, "result_bytes");
        if (wall is null || turns is null || caps is null || bytes is null) return null;
        if (wall < 60 || wall > 3600) return null;
        if (turns < 1 || turns > 32) return null;
        if (caps < 0 || caps > 100) return null;
        if (bytes < 1024 || bytes > 1_048_576) return null;
        return new RunBudget((int)turns.Value, Limits.V1_5.DefaultRunTotalTokenBudget, (int)wall.Value, (int)caps.Value, (int)bytes.Value);
    }

    // ---- permission_request ----------------------------------------------

    private PermissionRequest? ValidatePermission(JsonNode? node)
    {
        if (node is not JsonObject p) return null;
        if (p["capabilities"] is not JsonArray caps) return null;
        if (caps.Count > 64) return null;
        var stableIds = new List<string>();
        foreach (var c in caps)
        {
            if (c is not JsonObject co) return null;
            var sk = AsString(co, "source_kind");
            var sid = AsString(co, "source_id");
            var stable = AsString(co, "capability_stable_id");
            var sha = AsString(co, "schema_sha256");
            if (sk is null || !SourceKinds.Contains(sk)) return null;
            if (string.IsNullOrWhiteSpace(sid) || string.IsNullOrWhiteSpace(stable)) return null;
            if (sha is not null && !Hex64.IsMatch(sha)) return null;
            stableIds.Add(stable);
        }
        return new PermissionRequest(stableIds, "read-only");
    }

    // ---- helpers ----------------------------------------------------------

    private static string? ScanForbiddenKeys(JsonNode? node, string path)
    {
        if (node is JsonObject obj)
        {
            foreach (var kv in obj)
            {
                if (ForbiddenKeys.Contains(kv.Key))
                    return kv.Key;
                var found = ScanForbiddenKeys(kv.Value, path + "." + kv.Key);
                if (found is not null) return found;
            }
        }
        else if (node is JsonArray arr)
        {
            for (var i = 0; i < arr.Count; i++)
            {
                var found = ScanForbiddenKeys(arr[i], path + $"[{i}]");
                if (found is not null) return found;
            }
        }
        return null;
    }

    private static string? AsString(JsonObject o, string key)
        => o.TryGetPropertyValue(key, out var v) && v is JsonValue jv ? jv.GetValue<string>() : null;

    private static long? GetLong(JsonObject o, string key)
        => o.TryGetPropertyValue(key, out var v) && v is JsonValue jv && jv.TryGetValue<long>(out var l) ? l : null;

    private static bool GetBool(JsonObject o, string key, bool fallback)
        => o.TryGetPropertyValue(key, out var v) && v is JsonValue jv && jv.TryGetValue<bool>(out var b) ? b : fallback;

    private static int[]? GetIntArray(JsonObject o, string key)
    {
        if (o.TryGetPropertyValue(key, out var v) && v is JsonArray arr)
        {
            var result = new List<int>();
            foreach (var item in arr)
                if (item is JsonValue iv && iv.TryGetValue<long>(out var l))
                    result.Add((int)l);
            return result.ToArray();
        }
        return null;
    }

    private static T? AsEnum<T>(JsonObject o, string key, HashSet<string> allowed) where T : struct
    {
        var s = AsString(o, key);
        return s is not null && allowed.Contains(s) ? (T)Enum.Parse(typeof(T), s, true) : null;
    }

    private static DateTimeOffset? ParseUtc(string? s)
        => string.IsNullOrEmpty(s) ? null
            : (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var v) ? v : null);

    private static DateTimeOffset? CombineLocal(string? date, string? time)
    {
        if (date is null || time is null) return null;
        var combined = $"{date}T{time}:00Z";
        return ParseUtc(combined);
    }
}
