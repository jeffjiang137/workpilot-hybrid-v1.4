using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation;
using WorkPilot.Domain.Automation.Run;
using WorkPilot.Domain.Automation.Run.Interpreter;
using Xunit;

namespace WorkPilot.Domain.Tests.Automation.Run;

/// <summary>End-to-end deterministic interpretation (T10 / AUT-004, RUN-002/004): linear execution,
/// Condition branch pruning, budget-exhaustion halting (RUN-A13), cooperative cancellation, and waits.</summary>
public class WorkflowInterpreterTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    private static readonly AutomationRevisionId Rev = AutomationRevisionId.Parse("rev_1");
    private static readonly RunSnapshotId Snap = RunSnapshotId.Parse("snap_1");

    private static AutomationRun RunningRun() =>
        AutomationRun.Create(RunId.Parse("run_1"), Rev, Snap, RunTriggerKind.Interval, Now, Now).MarkRunning(Now);

    private static WorkflowNode Node(string id, string kind, JsonObject? payload = null) =>
        new(id, id, kind, 60, false, payload);

    private static WorkflowNode Condition(string id, JsonNode condition) =>
        Node(id, "condition", new JsonObject { ["condition"] = condition });

    private sealed class ScriptedExecutor : INodeEffectExecutor
    {
        public NodeCost Cost = new(ModelTurns: 1, CapabilityCalls: 0, ResultBytes: 100, WallClockSeconds: 1);
        public readonly Dictionary<string, NodeEffectResult> ByNode = new(StringComparer.Ordinal);
        public NodeEffectResult Default = new(StepRunStatus.Succeeded);
        public bool ThrowCancelled;

        public NodeCost Estimate(WorkflowNode node) => Cost;

        public NodeEffectResult ExecuteNode(WorkflowNode node, VariableStore inputVars, AutomationRun run, StepRun step, CancellationToken ct)
        {
            if (ThrowCancelled) throw new OperationCanceledException();
            return ByNode.TryGetValue(node.NodeId, out var r) ? r : Default;
        }
    }

    private static InterpretationResult Run(
        WorkflowDefinition wf, INodeEffectExecutor exec, RunBudget budget,
        VariableStore? vars = null, bool cancellationRequested = false, AutomationRun? run = null)
        => WorkflowInterpreter.Interpret(
            wf, run ?? RunningRun(), Array.Empty<StepRun>(), budget, vars ?? new VariableStore(),
            exec, new SequentialIdGenerator(), new FakeClock(Now), CancellationToken.None, cancellationRequested);

    private static RunBudget Generous => new(10, 1_000_000, 10_000, 10, 1_000_000);

    [Fact]
    public void Linear_dag_executes_every_node_and_completes()
    {
        var wf = new WorkflowDefinition(1, "a",
            new[] { Node("a", "agent_prompt"), Node("b", "capability_call"), Node("c", "notification") },
            new[] { new WorkflowEdge("a", "b", "next"), new WorkflowEdge("b", "c", "next") });

        var result = Run(wf, new ScriptedExecutor(), Generous);

        Assert.False(result.Halted);
        Assert.Equal(RunStatus.Completed, result.Run.Status);
        Assert.Equal(3, result.Steps.Count);
        Assert.All(result.Steps, s => Assert.Equal(StepRunStatus.Succeeded, s.Status));
        Assert.Contains(result.Events, e => e.Code == "RUN_COMPLETED");
    }

    [Fact]
    public void Condition_true_branch_prunes_false_branch()
    {
        var cond = new JsonObject { ["path"] = "trigger.go", ["op"] = "eq", ["value"] = true };
        var wf = new WorkflowDefinition(1, "cond",
            new[] { Condition("cond", cond), Node("t", "notification"), Node("f", "notification") },
            new[] { new WorkflowEdge("cond", "t", "true"), new WorkflowEdge("cond", "f", "false") });

        var vars = new VariableStore(triggerVars: new Dictionary<string, JsonNode> { ["go"] = true });
        var result = Run(wf, new ScriptedExecutor(), Generous, vars);

        Assert.Equal(RunStatus.Completed, result.Run.Status);
        Assert.Equal(StepRunStatus.Succeeded, result.Steps.Single(s => s.NodeId == "t").Status);
        Assert.Equal(StepRunStatus.Skipped, result.Steps.Single(s => s.NodeId == "f").Status);
    }

    [Fact]
    public void Condition_false_branch_prunes_true_branch()
    {
        var cond = new JsonObject { ["path"] = "trigger.go", ["op"] = "eq", ["value"] = true };
        var wf = new WorkflowDefinition(1, "cond",
            new[] { Condition("cond", cond), Node("t", "notification"), Node("f", "notification") },
            new[] { new WorkflowEdge("cond", "t", "true"), new WorkflowEdge("cond", "f", "false") });

        var vars = new VariableStore(triggerVars: new Dictionary<string, JsonNode> { ["go"] = false });
        var result = Run(wf, new ScriptedExecutor(), Generous, vars);

        Assert.Equal(StepRunStatus.Skipped, result.Steps.Single(s => s.NodeId == "t").Status);
        Assert.Equal(StepRunStatus.Succeeded, result.Steps.Single(s => s.NodeId == "f").Status);
    }

    [Fact]
    public void Malformed_condition_fails_the_run_closed()
    {
        var wf = new WorkflowDefinition(1, "cond",
            new[] { Node("cond", "condition", new JsonObject()), Node("t", "notification") }, // no "condition" key
            new[] { new WorkflowEdge("cond", "t", "true") });

        var result = Run(wf, new ScriptedExecutor(), Generous);

        Assert.True(result.Halted);
        Assert.Equal(RunStatus.Failed, result.Run.Status);
        Assert.Contains(result.Events, e => e.Code == "RUN_CONDITION_FAILED");
    }

    [Fact]
    public void Budget_exhaustion_halts_and_skips_remaining(/* RUN-A13 */)
    {
        var wf = new WorkflowDefinition(1, "a",
            new[] { Node("a", "agent_prompt"), Node("b", "agent_prompt"), Node("c", "agent_prompt") },
            new[] { new WorkflowEdge("a", "b", "next"), new WorkflowEdge("b", "c", "next") });

        // Only one model turn in the whole run; each node estimates one turn.
        var budget = new RunBudget(MaxModelTurns: 1, MaxTotalTokens: 1_000_000,
            MaxWallClockSeconds: 10_000, MaxCapabilityCalls: 10, MaxResultBytes: 1_000_000);

        var result = Run(wf, new ScriptedExecutor(), budget);

        Assert.True(result.Halted);
        Assert.Equal(RunStatus.Failed, result.Run.Status);
        Assert.Equal(StepRunStatus.Succeeded, result.Steps.Single(s => s.NodeId == "a").Status);
        Assert.Equal(StepRunStatus.Failed, result.Steps.Single(s => s.NodeId == "b").Status);
        Assert.DoesNotContain(result.Steps, s => s.NodeId == "c"); // never scheduled
        Assert.Contains(result.Events, e => e.Code == "RUN_BUDGET_EXCEEDED");
    }

    [Fact]
    public void Cancellation_request_stops_scheduling_and_skips_downstream()
    {
        var wf = new WorkflowDefinition(1, "a",
            new[] { Node("a", "agent_prompt"), Node("b", "agent_prompt"), Node("c", "agent_prompt") },
            new[] { new WorkflowEdge("a", "b", "next"), new WorkflowEdge("b", "c", "next") });

        var result = Run(wf, new ScriptedExecutor(), Generous, cancellationRequested: true);

        Assert.True(result.Halted);
        Assert.Equal(RunStatus.Cancelled, result.Run.Status);
        Assert.Equal(StepRunStatus.Cancelled, result.Steps.Single(s => s.NodeId == "a").Status);
        Assert.Equal(StepRunStatus.Skipped, result.Steps.Single(s => s.NodeId == "b").Status);
        Assert.Equal(StepRunStatus.Skipped, result.Steps.Single(s => s.NodeId == "c").Status);
    }

    [Fact]
    public void Node_output_key_is_declared_and_visible_to_later_condition()
    {
        var exec = new ScriptedExecutor();
        exec.ByNode["a"] = new NodeEffectResult(StepRunStatus.Succeeded, OutputKey: "score", OutputValue: JsonValue.Create(42));
        var cond = new JsonObject { ["path"] = "vars.score", ["op"] = "gte", ["value"] = 40 };

        var wf = new WorkflowDefinition(1, "a",
            new[] { Node("a", "agent_prompt"), Condition("cond", cond), Node("hi", "notification"), Node("lo", "notification") },
            new[]
            {
                new WorkflowEdge("a", "cond", "next"),
                new WorkflowEdge("cond", "hi", "true"),
                new WorkflowEdge("cond", "lo", "false")
            });

        var result = Run(wf, exec, Generous);

        Assert.Equal(RunStatus.Completed, result.Run.Status);
        Assert.Equal(StepRunStatus.Succeeded, result.Steps.Single(s => s.NodeId == "hi").Status);
        Assert.Equal(StepRunStatus.Skipped, result.Steps.Single(s => s.NodeId == "lo").Status);
    }

    [Fact]
    public void Waiting_delay_releases_the_run()
    {
        var exec = new ScriptedExecutor();
        exec.ByNode["a"] = new NodeEffectResult(StepRunStatus.WaitingDelay, ResumeAtUtc: Now.AddHours(1));
        var wf = new WorkflowDefinition(1, "a",
            new[] { Node("a", "delay"), Node("b", "notification") },
            new[] { new WorkflowEdge("a", "b", "next") });

        var result = Run(wf, exec, Generous);

        Assert.True(result.Halted);
        Assert.Equal(RunStatus.WaitingDelay, result.Run.Status);
        Assert.Equal(StepRunStatus.WaitingDelay, result.Steps.Single(s => s.NodeId == "a").Status);
        Assert.DoesNotContain(result.Steps, s => s.NodeId == "b");
    }

    [Fact]
    public void Executor_thrown_cancellation_marks_step_cancelled()
    {
        var exec = new ScriptedExecutor { ThrowCancelled = true };
        var wf = new WorkflowDefinition(1, "a", new[] { Node("a", "agent_prompt") }, Array.Empty<WorkflowEdge>());

        var result = Run(wf, exec, Generous);

        Assert.True(result.Halted);
        Assert.Equal(RunStatus.Cancelled, result.Run.Status);
        Assert.Equal(StepRunStatus.Cancelled, result.Steps.Single(s => s.NodeId == "a").Status);
    }

    [Fact]
    public void Empty_workflow_throws_domain_exception()
    {
        var wf = new WorkflowDefinition(1, "a", Array.Empty<WorkflowNode>(), Array.Empty<WorkflowEdge>());
        Assert.Throws<DomainException>(() => Run(wf, new ScriptedExecutor(), Generous));
    }

    [Fact]
    public void Missing_entry_node_throws_domain_exception()
    {
        var wf = new WorkflowDefinition(1, "missing", new[] { Node("a", "agent_prompt") }, Array.Empty<WorkflowEdge>());
        Assert.Throws<DomainException>(() => Run(wf, new ScriptedExecutor(), Generous));
    }
}
