using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Domain.Automation;
using WorkPilot.Domain.Automation.Run;
using WorkPilot.Domain.Automation.Run.Interpreter;

namespace WorkPilot.Application.Automation.Run.Executors;

/// <summary>
/// Executes an <c>agent_prompt</c> node (doc 03 §3.2): builds a structured request from the instruction
/// template plus resolved input bindings and delegates the model call to <see cref="IAgentBackend"/>.
/// The backend owns the actual Expert invocation and capability scoping (T12/T17). Cancellation is
/// propagated: a cancelled backend throws <see cref="OperationCanceledException"/>, which the interpreter
/// converts into a Cancelled step. The node's <c>max_model_turns</c> is reserved up-front by the
/// interpreter via <see cref="Estimate"/> and echoed to the backend for enforcement.
/// </summary>
public sealed class AgentExecutor
{
    private const int ResultByteEstimate = 8 * 1024; // bounded per-output estimate for budget reservation

    private readonly IAgentBackend _backend;

    public AgentExecutor(IAgentBackend backend) => _backend = backend;

    public NodeCost Estimate(WorkflowNode node)
    {
        var maxTurns = Clamp(ReadLong(node, "max_model_turns", 1), 1, Limits.V1_5.MaxAgentModelTurns);
        var wallClock = Clamp(node.TimeoutSeconds, 0, Limits.V1_5.MaxStepTimeoutSeconds);
        return new NodeCost(maxTurns, CapabilityCalls: 0, ResultBytes: ResultByteEstimate, WallClockSeconds: wallClock);
    }

    public NodeEffectResult ExecuteNode(WorkflowNode node, VariableStore inputVars, AutomationRun run, StepRun step, CancellationToken ct)
        => ExecuteNodeAsync(node, inputVars, run, ct).GetAwaiter().GetResult();

    private async Task<NodeEffectResult> ExecuteNodeAsync(WorkflowNode node, VariableStore inputVars, AutomationRun run, CancellationToken ct)
    {
        // Dry-run (RUN-005): never call the model backend. Produce a plan summary describing the
        // would-be agent invocation and return Succeeded so the planner walks the whole workflow.
        if (run.IsDryRun)
            return new NodeEffectResult(StepRunStatus.Succeeded, OutputKey: "plan", OutputValue: BuildDryRunPlan(node));

        var template = node.Payload?["instruction_template"]?.GetValue<string>() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(template) || template.Length > Limits.V1_5.MaxAgentInstructionLength)
            return new NodeEffectResult(StepRunStatus.Failed,
                ErrorCode: RunErrors.AgentInstructionMissingError(node.NodeId).Code);

        var outputKey = node.Payload?["output_key"]?.GetValue<string>();
        var capabilityMode = node.Payload?["capability_mode"]?.GetValue<string>() ?? "none";
        var declaredCapabilities = ReadStringArray(node.Payload, "declared_capabilities");
        var maxTurns = Clamp(ReadLong(node, "max_model_turns", 1), 1, Limits.V1_5.MaxAgentModelTurns);

        // Resolve input bindings (each a $ref path) against the run-scoped variable store.
        var bindings = ReadStringArray(node.Payload, "input_bindings");
        var inputs = new List<AgentInputVariable>(bindings.Count);
        foreach (var binding in bindings)
        {
            if (!inputVars.TryResolve(binding, out var value) || value is null)
                return new NodeEffectResult(StepRunStatus.Failed,
                    ErrorCode: RunErrors.VariableBindingFailedError(node.NodeId, binding).Code);
            inputs.Add(new AgentInputVariable(binding, value.DeepClone()));
        }

        // Substitute {{$ref:path}} tokens in the instruction with resolved values. Any resolvable path is
        // permitted here (trigger/run/system/vars); secrets are already blocked by the store.
        var instruction = TemplateRenderer.Render(template, inputVars, _ => true, out var badRef);
        if (instruction is null)
            return new NodeEffectResult(StepRunStatus.Failed,
                ErrorCode: RunErrors.VariableBindingFailedError(node.NodeId, badRef ?? "(instruction)").Code);

        var request = new AgentInvocationRequest(
            NodeId: node.NodeId,
            Instruction: instruction,
            Inputs: inputs,
            MaxModelTurns: maxTurns,
            CapabilityMode: capabilityMode,
            DeclaredCapabilities: declaredCapabilities);

        AgentInvocationResult result;
        try
        {
            result = await _backend.InvokeAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw; // interpreter converts to a Cancelled step
        }

        if (!result.IsSuccess)
            return new NodeEffectResult(StepRunStatus.Failed,
                ErrorCode: RunErrors.AgentBackendFailedError(node.NodeId, result.ErrorCode).Code);

        if (string.IsNullOrWhiteSpace(outputKey))
            return new NodeEffectResult(StepRunStatus.Succeeded);

        return new NodeEffectResult(StepRunStatus.Succeeded,
            OutputKey: outputKey, OutputValue: result.OutputValue?.DeepClone());
    }

    private static long ReadLong(WorkflowNode node, string key, long fallback)
    {
        var raw = node.Payload?[key];
        return raw is not null && TryReadLong(raw, out var value) ? value : fallback;
    }

    // STJ's JsonValue.GetValue<long>() only succeeds for a long-backed node, so an int literal throws.
    // Probe int/long/double to read any numeric JSON value.
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

    private static int Clamp(long value, int min, int max) => (int)Math.Max(min, Math.Min(max, value));

    /// <summary>Builds the dry-run plan summary for an <c>agent_prompt</c> node (no model call performed).</summary>
    private static JsonObject BuildDryRunPlan(WorkflowNode node)
    {
        var maxTurns = Clamp(ReadLong(node, "max_model_turns", 1), 1, Limits.V1_5.MaxAgentModelTurns);
        var declaredCapabilities = ReadStringArray(node.Payload, "declared_capabilities");
        return new JsonObject
        {
            ["dry_run"] = true,
            ["node_kind"] = "agent_prompt",
            ["max_model_turns"] = maxTurns,
            ["declared_capabilities"] = new JsonArray(declaredCapabilities.Select(x => (JsonNode)x).ToArray()),
            ["would_call_model"] = true
        };
    }
}
