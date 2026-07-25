using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation;
using WorkPilot.Domain.Automation.Run;
using WorkPilot.Domain.Automation.Run.Interpreter;
using WorkPilot.Application.Automation.Materialization;

namespace WorkPilot.Application.Automation.Run;

/// <summary>
/// Simulates one execution pass of an automation revision WITHOUT performing any side effect
/// (RUN-005 / AUT-A11). Builds an in-memory frozen snapshot and an in-memory <see cref="AutomationRun"/>
/// flagged <see cref="AutomationRun.IsDryRun"/>, then drives the pure <see cref="WorkflowInterpreter"/>
/// through the injected <see cref="INodeEffectExecutor"/>. Because every executor short-circuits on
/// dry-run, no permit is issued and no adapter / sink / model backend is ever touched — the returned
/// <see cref="DryRunPlan"/> is a pure description of the would-be execution. Dry-run runs are NEVER
/// persisted, so reconstructed runs from storage are always live (<see cref="AutomationRun.IsDryRun"/>
/// defaults to <c>false</c>).
/// </summary>
public sealed class DryRunPlanner
{
    private readonly IIdGenerator _ids;
    private readonly IClock _clock;
    private readonly INodeEffectExecutor _executor;

    public DryRunPlanner(IIdGenerator ids, IClock clock, INodeEffectExecutor executor)
    {
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    /// <summary>
    /// Produces a dry-run plan for <paramref name="revision"/>. Any workflow-level validation failure
    /// (empty workflow, missing entry node) is caught and surfaced as an invalid plan; node-level
    /// failures are recorded in the returned steps and mark <see cref="DryRunPlan.IsValid"/> false.
    /// </summary>
    public DryRunPlan Plan(AutomationRevision revision, CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        try
        {
            var expertRevisionId = revision.Binding.ExpertId is { } e
                ? ExpertRevisionId.Parse(e)
                : ExpertRevisionId.Parse("unbound");

            var snapshot = RunSnapshotFactory.Build(_ids, revision, expertRevisionId, revocationEpoch: 0, now);

            var run = AutomationRun.Create(
                RunId.Create(_ids),
                revision.Id,
                snapshot.Id,
                RunTriggerKind.Manual,
                now,
                now,
                isDryRun: true);

            var variables = BuildVariableStore(run, snapshot);

            // Capture each node's outcome so the plan can surface per-step summaries even though the
            // interpreter itself does not return them.
            var capturing = new PlanCapturingExecutor(_executor);

            var result = WorkflowInterpreter.Interpret(
                revision.Workflow, run, Array.Empty<StepRun>(), revision.Budget,
                variables, capturing, _ids, _clock, ct, cancellationRequested: false);

            var steps = capturing.Captured.Select(c => new DryRunStepPlan(
                c.NodeId, c.NodeKind, c.Status, c.PlanSummary)).ToList();

            var plannedSideEffects = steps.Count(s => s.IsSideEffecting);
            var isValid = result.Run.Status is RunStatus.Completed or RunStatus.Running
                or RunStatus.Queued or RunStatus.Claimed
                or RunStatus.WaitingDelay or RunStatus.WaitingApproval;

            return new DryRunPlan(
                isValid,
                result.Run.Status.ToString(),
                steps,
                plannedSideEffects > 0,
                plannedSideEffects,
                0,
                null);
        }
        catch (DomainException dex)
        {
            return DryRunPlan.Invalid(dex.Error.Code);
        }
    }

    /// <summary>
    /// Builds the run-scoped variable store (doc 03 §4), mirroring
    /// <see cref="WorkflowExecutionService"/>: the frozen binding snapshot provides <c>trigger.*</c>,
    /// the run header provides <c>run.*</c>, <c>system.now</c> carries the clock. No secret is ever
    /// placed in the store.
    /// </summary>
    private VariableStore BuildVariableStore(AutomationRun run, RunSnapshot snapshot)
    {
        var triggerVars = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        if (TryParseObject(snapshot.BindingSnapshotJson, out var binding))
            foreach (var kv in binding!)
                if (kv.Value is not null)
                    triggerVars[kv.Key] = kv.Value.DeepClone();

        var runVars = new Dictionary<string, JsonNode>(StringComparer.Ordinal)
        {
            ["id"] = JsonValue.Create(run.Id.Value),
            ["priority"] = JsonValue.Create(run.Priority),
            ["trigger_kind"] = JsonValue.Create(run.TriggerKind.ToStorage())
        };

        var systemVars = new Dictionary<string, JsonNode>(StringComparer.Ordinal)
        {
            ["now"] = JsonValue.Create(_clock.UtcNow.ToString("O"))
        };

        return new VariableStore(triggerVars, runVars, systemVars);
    }

    private static bool TryParseObject(string? json, out JsonObject? obj)
    {
        obj = null;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            obj = JsonNode.Parse(json) as JsonObject;
            return obj is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Wraps an <see cref="INodeEffectExecutor"/> and records each node's outcome for plan capture.</summary>
    private sealed class PlanCapturingExecutor : INodeEffectExecutor
    {
        private readonly INodeEffectExecutor _inner;
        public readonly List<(string NodeId, string NodeKind, StepRunStatus Status, JsonNode? PlanSummary)> Captured = new();

        public PlanCapturingExecutor(INodeEffectExecutor inner) => _inner = inner;

        public NodeCost Estimate(WorkflowNode node) => _inner.Estimate(node);

        public NodeEffectResult ExecuteNode(WorkflowNode node, VariableStore inputVars, AutomationRun run, StepRun step, CancellationToken ct)
        {
            var result = _inner.ExecuteNode(node, inputVars, run, step, ct);
            Captured.Add((node.NodeId, node.Kind, result.Status, result.OutputValue));
            return result;
        }
    }
}
