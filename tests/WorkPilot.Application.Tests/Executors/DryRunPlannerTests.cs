using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using WorkPilot.Application.Automation.Run;
using WorkPilot.Application.Automation.Run.Executors;
using WorkPilot.Application.Automation.Run.Permit;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation;
using WorkPilot.Domain.Automation.Run;
using WorkPilot.Domain.Automation.Run.Interpreter;
using Xunit;

namespace WorkPilot.Application.Tests.Executors;

/// <summary>Dry-run planner (T22b, RUN-005 / AUT-A11): full-workflow simulation with zero side effects.</summary>
public class DryRunPlannerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    private static WorkflowNode AgentNode() =>
        new("agent_1", "Agent", "agent_prompt", 60, false, new JsonObject
        {
            ["instruction_template"] = "Summarize the trigger",
            ["output_key"] = "result",
            ["max_model_turns"] = 4,
            ["declared_capabilities"] = new JsonArray()
        });

    private static WorkflowNode CapabilityNode() =>
        new("cap_1", "Send", "capability_call", 60, false, new JsonObject
        {
            ["capability"] = new JsonObject
            {
                ["source_kind"] = "connector",
                ["source_id"] = "acct_1",
                ["stable_id"] = "send_email",
                ["schema_sha256"] = "sha256abc",
                ["risk"] = "medium"
            },
            ["arguments"] = new JsonObject { ["to"] = "a@b.c", ["body"] = "hi" }
        });

    private static WorkflowNode NotificationNode() =>
        new("notif_1", "Notify", "notification", 30, false, new JsonObject
        {
            ["template"] = "Dry-run notification",
            ["title"] = "Dry Run",
            ["required"] = false,
            ["safe_output_keys"] = new JsonArray()
        });

    private static AutomationRevision SampleRevision() =>
        AutomationRevision.Create(
            AutomationRevisionId.Parse("rev_1"),
            AutomationId.Parse("auto_1"),
            1,
            new TriggerDefinition("trig_1", TriggerType.Manual, true, null, null, null, null, null, null, null, null, null, null, null),
            new WorkflowDefinition(1, "agent_1",
                new[] { AgentNode(), CapabilityNode(), NotificationNode() },
                new[] { new WorkflowEdge("agent_1", "cap_1", "next"), new WorkflowEdge("cap_1", "notif_1", "next") }),
            new AutomationBinding(null, "exp_1"),
            new RunBudget(10, 1_000_000, 3600, 5, 1_000_000),
            OverlapPolicy.Skip,
            MissedRunPolicy.Skip,
            new PermissionRequest(new[] { "send_email" }, "read-only"),
            Now);

    private static NodeEffectExecutor WiredExecutor(ManagedPermitCore core, FakeCapabilityAdapter adapter, ISideEffectJournal journal) =>
        new(
            new ScriptedAgentBackend(),
            new RecordingNotificationSink(),
            new PermitIssuer(core, new FakeClock(Now), new SequentialIdGenerator()),
            new FakeAdapterResolver { Adapter = adapter },
            journal,
            new FakeClock(Now),
            () => 0);

    [Fact]
    public void Full_workflow_is_planned_without_any_side_effect()
    {
        var core = new ManagedPermitCore(new FakeClock(Now));
        core.CurrentRevocationEpoch = 0;
        var adapter = new FakeCapabilityAdapter();
        var journal = new InMemorySideEffectJournal();
        var backend = new ScriptedAgentBackend();
        var executor = new NodeEffectExecutor(
            backend, new RecordingNotificationSink(),
            new PermitIssuer(core, new FakeClock(Now), new SequentialIdGenerator()),
            new FakeAdapterResolver { Adapter = adapter },
            journal, new FakeClock(Now), () => 0);

        var planner = new DryRunPlanner(new SequentialIdGenerator(), new FakeClock(Now), executor);
        var plan = planner.Plan(SampleRevision());

        Assert.True(plan.IsValid);
        Assert.Equal("Completed", plan.FinalStatus);
        Assert.Equal(3, plan.Steps.Count);
        Assert.Equal(0, plan.RealSendCount);            // AUT-A11: dry-run never performs I/O
        Assert.True(plan.WouldSendSideEffects);
        Assert.Equal(2, plan.PlannedSideEffectCount);   // capability_call + notification

        var cap = plan.Steps.Single(s => s.NodeKind == "capability_call");
        var capPlan = Assert.IsType<JsonObject>(cap.PlanSummary);
        Assert.Equal(true, capPlan["dry_run"]?.GetValue<bool>());
        Assert.Equal("send_email", capPlan["capability_stable_id"]?.GetValue<string>());
        Assert.Equal(true, capPlan["would_send"]?.GetValue<bool>());

        // Hard proof no external I/O occurred.
        Assert.Equal(0, adapter.IoCalls);   // High write capability never sent
        Assert.Null(backend.LastRequest);   // model backend never called
    }

    [Fact]
    public void Delay_node_is_planned_not_waited_and_downstream_still_simulated()
    {
        var delayNode = new WorkflowNode("delay_1", "Wait", "delay", 30, false, new JsonObject
        {
            ["delay_seconds"] = 600
        });
        var revision = AutomationRevision.Create(
            AutomationRevisionId.Parse("rev_2"),
            AutomationId.Parse("auto_2"),
            1,
            new TriggerDefinition("trig_2", TriggerType.Manual, true, null, null, null, null, null, null, null, null, null, null, null),
            new WorkflowDefinition(1, "agent_1",
                new[] { AgentNode(), delayNode, NotificationNode() },
                new[] { new WorkflowEdge("agent_1", "delay_1", "next"), new WorkflowEdge("delay_1", "notif_1", "next") }),
            new AutomationBinding(null, "exp_2"),
            new RunBudget(10, 1_000_000, 3600, 5, 1_000_000),
            OverlapPolicy.Skip,
            MissedRunPolicy.Skip,
            new PermissionRequest(System.Array.Empty<string>(), "read-only"),
            Now);

        var executor = WiredExecutor(new ManagedPermitCore(new FakeClock(Now)) { CurrentRevocationEpoch = 0 }, new FakeCapabilityAdapter(), new InMemorySideEffectJournal());
        var planner = new DryRunPlanner(new SequentialIdGenerator(), new FakeClock(Now), executor);
        var plan = planner.Plan(revision);

        Assert.True(plan.IsValid);
        Assert.Equal("Completed", plan.FinalStatus);
        Assert.Equal(3, plan.Steps.Count); // delay did NOT halt the walk

        var delay = plan.Steps.Single(s => s.NodeKind == "delay");
        var delayPlan = Assert.IsType<JsonObject>(delay.PlanSummary);
        Assert.Equal(true, delayPlan["would_wait"]?.GetValue<bool>());
        Assert.Equal(600L, delayPlan["delay_seconds"]?.GetValue<long>());
    }

    [Fact]
    public void Empty_workflow_yields_invalid_plan_with_error_code()
    {
        var revision = AutomationRevision.Create(
            AutomationRevisionId.Parse("rev_3"),
            AutomationId.Parse("auto_3"),
            1,
            new TriggerDefinition("trig_3", TriggerType.Manual, true, null, null, null, null, null, null, null, null, null, null, null),
            new WorkflowDefinition(1, "agent_1", System.Array.Empty<WorkflowNode>(), System.Array.Empty<WorkflowEdge>()),
            new AutomationBinding(null, "exp_3"),
            new RunBudget(10, 1_000_000, 3600, 5, 1_000_000),
            OverlapPolicy.Skip,
            MissedRunPolicy.Skip,
            new PermissionRequest(System.Array.Empty<string>(), "read-only"),
            Now);

        var executor = WiredExecutor(new ManagedPermitCore(new FakeClock(Now)) { CurrentRevocationEpoch = 0 }, new FakeCapabilityAdapter(), new InMemorySideEffectJournal());
        var planner = new DryRunPlanner(new SequentialIdGenerator(), new FakeClock(Now), executor);
        var plan = planner.Plan(revision);

        Assert.False(plan.IsValid);
        Assert.False(string.IsNullOrEmpty(plan.ErrorCode));
        Assert.Equal(0, plan.RealSendCount);
    }
}
