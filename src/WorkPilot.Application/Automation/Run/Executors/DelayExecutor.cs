using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Domain.Automation;
using WorkPilot.Domain.Automation.Run;
using WorkPilot.Domain.Automation.Run.Interpreter;

namespace WorkPilot.Application.Automation.Run.Executors;

/// <summary>
/// Executes a <c>delay</c> node (doc 03 §3.5): computes <c>resume_at_utc</c> from the run clock
/// (<c>system.now</c>) and returns <see cref="StepRunStatus.WaitingDelay"/> so the interpreter releases
/// the worker and the materializer can requeue the run later. A delay consumes no budget — the wait is
/// tracked via <see cref="AutomationRun.ResumeAtUtc"/>, not the run's active wall-clock, so the
/// concurrency slot is freed (RUN-004).
/// </summary>
public sealed class DelayExecutor
{
    public NodeCost Estimate(WorkflowNode node)
        => new(ModelTurns: 0, CapabilityCalls: 0, ResultBytes: 0, WallClockSeconds: 0);

    public NodeEffectResult ExecuteNode(WorkflowNode node, VariableStore inputVars, AutomationRun run, StepRun step, System.Threading.CancellationToken ct)
    {
        // Dry-run (RUN-005): never wait. Produce a plan summary and return Succeeded (not
        // WaitingDelay) so the planner continues through the rest of the workflow.
        if (run.IsDryRun)
            return new NodeEffectResult(StepRunStatus.Succeeded, OutputKey: "plan", OutputValue: BuildDryRunPlan(node));

        var delaySeconds = ReadDelaySeconds(node);
        if (delaySeconds is null)
            return new NodeEffectResult(StepRunStatus.Failed,
                ErrorCode: RunErrors.DelayInvalidError(node.NodeId, "delay_seconds missing or out of range").Code);

        if (!inputVars.TryResolve("system.now", out var nowNode) || nowNode is null ||
            nowNode.GetValueKind() != JsonValueKind.String ||
            !DateTimeOffset.TryParse(((JsonValue)nowNode).GetValue<string>(), out var now))
            return new NodeEffectResult(StepRunStatus.Failed,
                ErrorCode: RunErrors.DelayClockInvalidError(node.NodeId).Code);

        var resumeAtUtc = now.AddSeconds(delaySeconds.Value);
        return new NodeEffectResult(StepRunStatus.WaitingDelay, ResumeAtUtc: resumeAtUtc);
    }

    private static long? ReadDelaySeconds(WorkflowNode node)
    {
        var raw = node.Payload?["delay_seconds"];
        if (raw is null || !TryReadLong(raw, out var value)) return null;
        if (value < Limits.V1_5.MinDelaySeconds || value > Limits.V1_5.MaxDelaySeconds) return null;
        return value;
    }

    // STJ's JsonValue.GetValue<long>() only succeeds for a long-backed node, so an int literal
    // (e.g. JSON 600) throws. Probe int/long/double to read any numeric JSON value (RUN-A-style robustness).
    private static bool TryReadLong(JsonNode raw, out long value)
    {
        value = 0;
        if (raw is JsonValue jv)
        {
            if (jv.TryGetValue<int>(out var i)) { value = i; return true; }
            if (jv.TryGetValue<long>(out var l)) { value = l; return true; }
            if (jv.TryGetValue<double>(out var d)) { value = (long)d; return true; }
        }
        return false;
    }

    /// <summary>Builds the dry-run plan summary for a <c>delay</c> node (no wait performed).</summary>
    private static JsonObject BuildDryRunPlan(WorkflowNode node)
    {
        var delaySeconds = ReadDelaySeconds(node);
        return new JsonObject
        {
            ["dry_run"] = true,
            ["node_kind"] = "delay",
            ["delay_seconds"] = (JsonNode?)(delaySeconds.HasValue ? (long)delaySeconds.Value : null),
            ["would_wait"] = true
        };
    }
}
