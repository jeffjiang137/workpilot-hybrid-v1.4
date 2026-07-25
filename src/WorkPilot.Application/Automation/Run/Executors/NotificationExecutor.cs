using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Domain.Automation;
using WorkPilot.Domain.Automation.Run;
using WorkPilot.Domain.Automation.Run.Interpreter;

namespace WorkPilot.Application.Automation.Run.Executors;

/// <summary>
/// Executes a <c>notification</c> node (doc 03 §3.6). Renders a fixed template by resolving only an
/// explicit allow-list of <c>{{$ref:path}}</c> references — run metadata and <c>vars.*</c> keys the node
/// explicitly marked safe — so no secret or free-text model output can reach the toast (RUN-008). The
/// rendered string (truncated to 200 chars) is handed to <see cref="INotificationSink"/>, which never
/// sees the variable store. An optional notification that fails to deliver is a Warning and does not
/// fail the run; only <c>required=true</c> escalates to a run failure.
/// </summary>
public sealed class NotificationExecutor
{
    private readonly INotificationSink _sink;

    public NotificationExecutor(INotificationSink sink) => _sink = sink;

    public NodeCost Estimate(WorkflowNode node)
        => new(ModelTurns: 0, CapabilityCalls: 0, ResultBytes: Limits.V1_5.MaxNotificationBodyLength, WallClockSeconds: 0);

    public NodeEffectResult ExecuteNode(WorkflowNode node, VariableStore inputVars, AutomationRun run, StepRun step, CancellationToken ct)
        => ExecuteNodeAsync(node, inputVars, run, ct).GetAwaiter().GetResult();

    private async Task<NodeEffectResult> ExecuteNodeAsync(WorkflowNode node, VariableStore inputVars, AutomationRun run, CancellationToken ct)
    {
        // Dry-run (RUN-005): never call the sink. Render the template (pure) to show what would be
        // sent and return Succeeded so the planner walks the whole workflow.
        if (run.IsDryRun)
            return new NodeEffectResult(StepRunStatus.Succeeded, OutputKey: "plan", OutputValue: BuildDryRunPlan(node, inputVars));

        var payload = node.Payload;
        var template = payload?["template"]?.GetValue<string>() ?? string.Empty;
        var title = payload?["title"]?.GetValue<string>();
        var required = payload?["required"]?.GetValue<bool>() ?? false;
        var safeKeys = ReadStringArray(payload, "safe_output_keys");

        var rendered = TemplateRenderer.Render(template, inputVars,
            path => IsNotificationRefAllowed(path, safeKeys), out var badRef);
        if (rendered is null)
            return new NodeEffectResult(StepRunStatus.Failed,
                ErrorCode: RunErrors.NotificationRenderFailedError(node.NodeId, badRef ?? "(template)").Code);

        if (rendered.Length > Limits.V1_5.MaxNotificationBodyLength)
            rendered = rendered.Substring(0, Limits.V1_5.MaxNotificationBodyLength);

        var content = new NotificationContent(
            Title: string.IsNullOrEmpty(title) ? "WorkPilot 自动化" : Truncate(title, Limits.V1_5.MaxNotificationTitleLength),
            Body: rendered);

        var delivery = await _sink.ShowAsync(content, ct).ConfigureAwait(false);
        if (!delivery.Delivered && required)
            return new NodeEffectResult(StepRunStatus.Failed,
                ErrorCode: RunErrors.NotificationDeliveryFailedError(node.NodeId).Code);

        return new NodeEffectResult(StepRunStatus.Succeeded);
    }

    /// <summary>
    /// Notification references are restricted to run metadata and explicitly-safe <c>vars.*</c> keys.
    /// <c>trigger.*</c>, <c>system.*</c> and <c>secrets.*</c> are never permitted, guaranteeing no
    /// business body or secret reaches the toast (RUN-008).
    /// </summary>
    private static bool IsNotificationRefAllowed(string path, IReadOnlyList<string> safeKeys)
    {
        if (path.StartsWith("run.", StringComparison.Ordinal))
        {
            var leaf = path.Substring(4);
            return leaf is "id" or "priority" or "trigger_kind";
        }
        if (path.StartsWith("vars.", StringComparison.Ordinal))
        {
            var key = path.Substring(5);
            return safeKeys.Contains(key);
        }
        return false;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonObject? payload, string key)
    {
        var arr = payload?[key] as JsonArray;
        if (arr is null) return Array.Empty<string>();
        var list = new List<string>(arr.Count);
        foreach (var item in arr)
            if (item is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                list.Add(s);
        return list;
    }

    private static string Truncate(string s, int max)
        => s.Length > max ? s.Substring(0, max) : s;

    /// <summary>Builds the dry-run plan summary for a <c>notification</c> node (no sink call performed).</summary>
    private static JsonObject BuildDryRunPlan(WorkflowNode node, VariableStore inputVars)
    {
        var payload = node.Payload;
        var template = payload?["template"]?.GetValue<string>() ?? string.Empty;
        var title = payload?["title"]?.GetValue<string>();
        var safeKeys = ReadStringArray(payload, "safe_output_keys");
        var rendered = TemplateRenderer.Render(template, inputVars,
            path => IsNotificationRefAllowed(path, safeKeys), out var badRef);
        return new JsonObject
        {
            ["dry_run"] = true,
            ["node_kind"] = "notification",
            ["title"] = (JsonNode?)title,
            ["rendered_body"] = (JsonNode?)(rendered ?? string.Empty),
            ["render_error_reference"] = (JsonNode?)badRef,
            ["would_notify"] = true
        };
    }
}
