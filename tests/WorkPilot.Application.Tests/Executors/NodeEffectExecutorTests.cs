using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using WorkPilot.Application.Automation.Run.Executors;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation;
using WorkPilot.Domain.Automation.Run;
using WorkPilot.Domain.Automation.Run.Interpreter;
using Xunit;

namespace WorkPilot.Application.Tests.Executors;

/// <summary>Dispatcher (T11) routing + end-to-end interpretation with the real executors.</summary>
public class NodeEffectExecutorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    private static readonly AutomationRevisionId Rev = AutomationRevisionId.Parse("rev_1");
    private static readonly RunSnapshotId Snap = RunSnapshotId.Parse("snap_1");

    private static RunBudget Generous => new(10, 1_000_000, 10_000, 10, 1_000_000);

    private static AutomationRun RunningRun() =>
        AutomationRun.Create(RunId.Parse("run_1"), Rev, Snap, RunTriggerKind.Interval, Now, Now).MarkRunning(Now);

    private static WorkflowNode Node(string id, string kind, JsonObject? payload = null) =>
        new(id, id, kind, 60, false, payload);

    private static VariableStore VarsWithClock() =>
        new(systemVars: new Dictionary<string, JsonNode> { ["now"] = JsonValue.Create(Now.ToString("O")) });

    private static StepRun DummyStep(string nodeId, string runId = "run_1") =>
        StepRun.Create(StepRunId.Create(new SequentialIdGenerator()), RunId.Parse(runId), nodeId, "capability_call",
            $"step:{nodeId}", $"digest:{nodeId}", 1, 1);

    private static InterpretationResult Run(WorkflowDefinition wf, INodeEffectExecutor exec, VariableStore? vars = null) =>
        WorkflowInterpreter.Interpret(wf, RunningRun(), Array.Empty<StepRun>(), Generous,
            vars ?? new VariableStore(), exec, new SequentialIdGenerator(), new FakeClock(Now), CancellationToken.None, false);

    // ---- dispatcher routing (unit) ----

    [Fact]
    public void Estimate_routes_by_kind()
    {
        var exec = new NodeEffectExecutor(new ScriptedAgentBackend(), new RecordingNotificationSink());
        var agent = Node("a", "agent_prompt", new JsonObject { ["instruction_template"] = "x", ["max_model_turns"] = 3 });
        var delay = Node("d", "delay", new JsonObject { ["delay_seconds"] = 600 });
        var notify = Node("n", "notification", new JsonObject { ["template"] = "hi" });

        Assert.Equal(3, exec.Estimate(agent).ModelTurns);
        Assert.Equal(0, exec.Estimate(delay).ModelTurns);
        Assert.Equal(Limits.V1_5.MaxNotificationBodyLength, exec.Estimate(notify).ResultBytes);
        Assert.Equal(0, exec.Estimate(Node("c", "capability_call")).ModelTurns);
    }

    [Fact]
    public void ExecuteNode_routes_agent_to_backend_and_notification_to_sink()
    {
        var backend = new ScriptedAgentBackend { NextResult = new(true, OutputValue: JsonValue.Create("ok"), ErrorCode: null) };
        var sink = new RecordingNotificationSink();
        var exec = new NodeEffectExecutor(backend, sink);

        var agent = Node("a", "agent_prompt", new JsonObject { ["instruction_template"] = "go", ["output_key"] = "r", ["input_bindings"] = new JsonArray() });
        var agentResult = exec.ExecuteNode(agent, VarsWithClock(), RunningRun(), DummyStep("a"), CancellationToken.None);
        Assert.Equal(StepRunStatus.Succeeded, agentResult.Status);
        Assert.NotNull(backend.LastRequest);

        var notify = Node("n", "notification", new JsonObject { ["template"] = "done", ["safe_output_keys"] = new JsonArray() });
        var notifyResult = exec.ExecuteNode(notify, VarsWithClock(), RunningRun(), DummyStep("n"), CancellationToken.None);
        Assert.Equal(StepRunStatus.Succeeded, notifyResult.Status);
        Assert.NotNull(sink.Last);
    }

    [Fact]
    public void Unsupported_capability_node_is_blocked_policy_closed()
    {
        var exec = new NodeEffectExecutor(new ScriptedAgentBackend(), new RecordingNotificationSink());
        var result = exec.ExecuteNode(Node("c", "capability_call"), VarsWithClock(), RunningRun(), DummyStep("c"), CancellationToken.None);

        Assert.Equal(StepRunStatus.BlockedPolicy, result.Status);
        Assert.Equal("RUN_NODE_KIND_NOT_SUPPORTED", result.ErrorCode);
    }

    [Fact]
    public void Unknown_kind_throws_in_estimate()
    {
        var exec = new NodeEffectExecutor(new ScriptedAgentBackend(), new RecordingNotificationSink());
        Assert.Throws<DomainException>(() => exec.Estimate(Node("x", "mystery_node")));
    }

    // ---- end-to-end interpretation with real executors ----

    [Fact]
    public void Delay_releases_run_and_persists_resume_time(/* RUN-004 recovery */)
    {
        var wf = new WorkflowDefinition(1, "a",
            new[] { Node("a", "delay", new JsonObject { ["delay_seconds"] = 600 }), Node("b", "notification", new JsonObject { ["template"] = "resumed" }) },
            new[] { new WorkflowEdge("a", "b", "next") });

        var result = Run(wf, new NodeEffectExecutor(new ScriptedAgentBackend(), new RecordingNotificationSink()), VarsWithClock());

        Assert.True(result.Halted);
        Assert.Equal(RunStatus.WaitingDelay, result.Run.Status);
        Assert.Equal(Now.AddSeconds(600), result.Run.ResumeAtUtc); // T11 fix: persisted, so the run can be requeued
        Assert.Equal("a", result.Run.CurrentNodeId);
        Assert.Equal(StepRunStatus.WaitingDelay, result.Steps.Single(s => s.NodeId == "a").Status);
        Assert.DoesNotContain(result.Steps, s => s.NodeId == "b");
    }

    [Fact]
    public void Capability_node_blocks_the_run_end_to_end()
    {
        var wf = new WorkflowDefinition(1, "a",
            new[] { Node("a", "capability_call") },
            Array.Empty<WorkflowEdge>());

        var result = Run(wf, new NodeEffectExecutor(new ScriptedAgentBackend(), new RecordingNotificationSink()));

        Assert.True(result.Halted);
        Assert.Equal(RunStatus.BlockedPolicy, result.Run.Status);
        Assert.Equal(StepRunStatus.BlockedPolicy, result.Steps.Single(s => s.NodeId == "a").Status);
    }
}
