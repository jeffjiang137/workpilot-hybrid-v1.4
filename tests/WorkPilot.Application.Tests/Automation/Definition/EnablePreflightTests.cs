using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Application.Automation.Definition;
using WorkPilot.Application.Permission.Policy;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation;
using WorkPilot.Domain.Automation.Validation;
using WorkPilot.Domain.PermissionGovernance;
using WorkPilot.Domain.PermissionGovernance.Evaluation;
using Xunit;

namespace WorkPilot.Application.Tests.Automation.Definition;

public class EnablePreflightTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

    private static EvaluationContext HealthyContext() => new(
        PolicySubject.AutomationPrincipal, "src-1", null, true, false, true, "space-1",
        true, false, 0, true, Now, "automation", "manual", 0, 0, "healthy");

    private static AutomationRevision BuildRevision(
        WorkflowDefinition workflow, TriggerDefinition trigger,
        RunBudget budget, AutomationBinding binding) =>
        new(
            AutomationRevisionId.Parse("rev-1"), AutomationId.Parse("auto-1"), 1,
            trigger, workflow, binding, budget,
            OverlapPolicy.Skip, MissedRunPolicy.Skip,
            new PermissionRequest(Array.Empty<string>(), "read-only"), "mock-hash", Now);

    private static WorkflowDefinition HealthyWorkflow(params WorkflowNode[] capNodes)
    {
        var nodes = new List<WorkflowNode>
        {
            new("start", "entry", "agent_prompt", 300, false, new JsonObject
            {
                ["instruction_template"] = "go",
                ["output_key"] = "prompt_out"
            })
        };
        nodes.AddRange(capNodes);
        var edges = new List<WorkflowEdge> { new("start", capNodes[0].NodeId, "next") };
        for (var i = 0; i < capNodes.Length - 1; i++)
            edges.Add(new WorkflowEdge(capNodes[i].NodeId, capNodes[i + 1].NodeId, "next"));
        return new WorkflowDefinition(1, "start", nodes, edges);
    }

    private static WorkflowNode CapNode(string id, string stableId, string risk, bool withIdentity = true)
    {
        var cap = new JsonObject
        {
            ["source_kind"] = "builtin",
            ["source_id"] = "src-1",
            ["stable_id"] = withIdentity ? stableId : null!,
            ["risk"] = risk
        };
        return new WorkflowNode(id, id, "capability_call", 300, false,
            new JsonObject { ["capability"] = cap });
    }

    private static readonly RunBudget OkBudget =
        new(8, 64 * 1024, 3600, 10, 1_048_576);
    private static readonly AutomationBinding OkBinding = new(null, "expert-1");

    [Fact]
    public async Task Healthy_automation_preflight_can_enable()
    {
        var wf = HealthyWorkflow(CapNode("c1", "cap.read", "low"), CapNode("c2", "cap.write", "medium"));
        var svc = new EnablePreflightService(new FakeCapabilityProbe(_ => PermissionDecisionKind.Allow));
        var result = await svc.RunAsync(BuildRevision(wf, new TriggerDefinition("t1", TriggerType.Manual, true, null, null, null, null, null, null, null, null, null, null, null), OkBudget, OkBinding), HealthyContext());

        Assert.True(result.CanEnable);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task Capability_denied_blocks_enable()
    {
        var wf = HealthyWorkflow(CapNode("c1", "cap.read", "low"), CapNode("c2", "cap.write", "medium"));
        var svc = new EnablePreflightService(new FakeCapabilityProbe(q =>
            q.StableId == "cap.write" ? PermissionDecisionKind.Deny : PermissionDecisionKind.Allow));
        var result = await svc.RunAsync(BuildRevision(wf, new TriggerDefinition("t1", TriggerType.Manual, true, null, null, null, null, null, null, null, null, null, null, null), OkBudget, OkBinding), HealthyContext());

        Assert.False(result.CanEnable);
        var err = Assert.Single(result.Errors);
        Assert.Equal(PreflightCodes.CapabilityDenied, err.Code);
        Assert.Equal("/workflow/nodes/c2", err.JsonPointer);
    }

    [Fact]
    public async Task Capability_ask_is_warning_not_blocking()
    {
        var wf = HealthyWorkflow(CapNode("c1", "cap.read", "low"), CapNode("c2", "cap.write", "medium"));
        var svc = new EnablePreflightService(new FakeCapabilityProbe(q =>
            q.StableId == "cap.write" ? PermissionDecisionKind.Ask : PermissionDecisionKind.Allow));
        var result = await svc.RunAsync(BuildRevision(wf, new TriggerDefinition("t1", TriggerType.Manual, true, null, null, null, null, null, null, null, null, null, null, null), OkBudget, OkBinding), HealthyContext());

        Assert.True(result.CanEnable); // warnings never block
        Assert.Empty(result.Errors);
        var warn = Assert.Single(result.Warnings);
        Assert.Equal(PreflightCodes.CapabilityAsk, warn.Code);
    }

    [Fact]
    public async Task Capability_deferred_is_warning()
    {
        var wf = HealthyWorkflow(CapNode("c1", "cap.read", "low"));
        var svc = new EnablePreflightService(new FakeCapabilityProbe(_ => PermissionDecisionKind.Defer));
        var result = await svc.RunAsync(BuildRevision(wf, new TriggerDefinition("t1", TriggerType.Manual, true, null, null, null, null, null, null, null, null, null, null, null), OkBudget, OkBinding), HealthyContext());

        Assert.True(result.CanEnable);
        var warn = Assert.Single(result.Warnings);
        Assert.Equal(PreflightCodes.CapabilityDeferred, warn.Code);
    }

    [Fact]
    public async Task Invalid_workflow_blocks_enable()
    {
        // entry points to a non-existent node => ENTRY_NOT_FOUND
        var wf = new WorkflowDefinition(1, "ghost", new[] { CapNode("c1", "cap.read", "low") }, Array.Empty<WorkflowEdge>());
        var svc = new EnablePreflightService(new FakeCapabilityProbe(_ => PermissionDecisionKind.Allow));
        var result = await svc.RunAsync(BuildRevision(wf, new TriggerDefinition("t1", TriggerType.Manual, true, null, null, null, null, null, null, null, null, null, null, null), OkBudget, OkBinding), HealthyContext());

        Assert.False(result.CanEnable);
        Assert.Contains(result.Errors, e => e.Code == ValidationCodes.EntryNotFound);
    }

    [Fact]
    public async Task Invalid_trigger_blocks_enable()
    {
        // Interval trigger without anchor_at_utc => INTERVAL_ANCHOR_MISSING
        var trigger = new TriggerDefinition("t1", TriggerType.Interval, true, null, null, null, 3600, null, null, null, null, null, null, null);
        var svc = new EnablePreflightService(new FakeCapabilityProbe(_ => PermissionDecisionKind.Allow));
        var result = await svc.RunAsync(BuildRevision(HealthyWorkflow(CapNode("c1", "cap.read", "low")), trigger, OkBudget, OkBinding), HealthyContext());

        Assert.False(result.CanEnable);
        Assert.Contains(result.Errors, e => e.Code == ValidationCodes.IntervalAnchorMissing);
    }

    [Fact]
    public async Task Out_of_range_budget_blocks_enable()
    {
        var badBudget = new RunBudget(8, 64 * 1024, 10, 10, 1_048_576); // wall 10 < 60
        var svc = new EnablePreflightService(new FakeCapabilityProbe(_ => PermissionDecisionKind.Allow));
        var result = await svc.RunAsync(BuildRevision(HealthyWorkflow(CapNode("c1", "cap.read", "low")), new TriggerDefinition("t1", TriggerType.Manual, true, null, null, null, null, null, null, null, null, null, null, null), badBudget, OkBinding), HealthyContext());

        Assert.False(result.CanEnable);
        var err = Assert.Single(result.Errors);
        Assert.Equal(PreflightCodes.BudgetWallInvalid, err.Code);
    }

    [Fact]
    public async Task Missing_capability_identity_is_error()
    {
        // a capability_call node with no stable_id => structural error, not silently skipped
        var badNode = CapNode("c1", "cap.read", "low", withIdentity: false);
        var wf = HealthyWorkflow(badNode);
        var svc = new EnablePreflightService(new FakeCapabilityProbe(_ => PermissionDecisionKind.Allow));
        var result = await svc.RunAsync(BuildRevision(wf, new TriggerDefinition("t1", TriggerType.Manual, true, null, null, null, null, null, null, null, null, null, null, null), OkBudget, OkBinding), HealthyContext());

        Assert.False(result.CanEnable);
        var err = Assert.Single(result.Errors);
        Assert.Equal(PreflightCodes.CapabilityIdentityMissing, err.Code);
    }

    [Fact]
    public async Task Empty_binding_blocks_enable()
    {
        var svc = new EnablePreflightService(new FakeCapabilityProbe(_ => PermissionDecisionKind.Allow));
        var result = await svc.RunAsync(BuildRevision(HealthyWorkflow(CapNode("c1", "cap.read", "low")), new TriggerDefinition("t1", TriggerType.Manual, true, null, null, null, null, null, null, null, null, null, null, null), OkBudget, new AutomationBinding(null, null)), HealthyContext());

        Assert.False(result.CanEnable);
        Assert.Contains(result.Errors, e => e.Code == PreflightCodes.BindingSpaceMissing);
    }
}

/// <summary>Fake probe: maps each capability query to a canned decision. Ignores the live policy store
/// so the Preflight structure + permission semantics can be tested in isolation.</summary>
internal sealed class FakeCapabilityProbe : ICapabilityPermissionProbe
{
    private readonly Func<CapabilityQuery, PermissionDecisionKind> _decide;
    public FakeCapabilityProbe(Func<CapabilityQuery, PermissionDecisionKind> decide) => _decide = decide;

    public Task<IReadOnlyList<EffectiveCapabilityView>> ProjectAsync(
        EvaluationContext context, IReadOnlyList<CapabilityQuery> queries, CancellationToken ct = default)
    {
        var views = queries.Select(q => new EffectiveCapabilityView(
            q.StableId, _decide(q), RiskLevel.Low, null, "fake", Array.Empty<DecisionTraceItem>())).ToList();
        return Task.FromResult((IReadOnlyList<EffectiveCapabilityView>)views);
    }
}
