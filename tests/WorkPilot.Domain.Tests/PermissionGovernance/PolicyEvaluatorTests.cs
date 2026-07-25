using System;
using System.Collections.Generic;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.PermissionGovernance;
using WorkPilot.Domain.PermissionGovernance.Evaluation;
using Xunit;

namespace WorkPilot.Domain.Tests.PermissionGovernance;

public class PolicyEvaluatorTests
{
    private static readonly SequentialIdGenerator Ids = new();
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

    // ---- statement builders ----

    private static PolicyStatement Allow(
        string sourceId, string capId,
        RiskLevel min = RiskLevel.Low, RiskLevel max = RiskLevel.Low,
        ResourceScope? scope = null,
        IReadOnlyList<PolicyCondition>? conditions = null,
        PolicySubject subject = PolicySubject.InteractiveUser,
        PolicyLayer layer = PolicyLayer.GlobalPolicy)
    {
        var vid = PolicyVersionId.Create(Ids);
        return PolicyStatement.Create(Ids, vid, true, PolicyEffect.Allow,
            new[] { subject }, Selector.ForId(sourceId).ToJson(), Selector.ForId(capId).ToJson(),
            min, max, scope, conditions ?? Array.Empty<PolicyCondition>(), 0);
    }

    private static PolicyStatement AllowAuto(string sourceId, string capId,
        RiskLevel min = RiskLevel.Low, RiskLevel max = RiskLevel.Low, ResourceScope? scope = null,
        IReadOnlyList<PolicyCondition>? conditions = null, PolicyLayer layer = PolicyLayer.GlobalPolicy)
        => Allow(sourceId, capId, min, max, scope, conditions, PolicySubject.AutomationPrincipal, layer);

    private static PolicyStatement DenyAll(RiskLevel min = RiskLevel.Low, RiskLevel max = RiskLevel.Critical)
    {
        var vid = PolicyVersionId.Create(Ids);
        return PolicyStatement.Create(Ids, vid, true, PolicyEffect.Deny,
            new[] { PolicySubject.InteractiveUser, PolicySubject.AutomationPrincipal, PolicySubject.SystemMaintenance },
            Selector.MatchAllJson, Selector.MatchAllJson, min, max, null, Array.Empty<PolicyCondition>(), 0);
    }

    private static PolicyStatement RawAllowWithUnknownCondition(string sourceId, string capId)
    {
        // Bypasses Create/Validate so we can exercise fail-closed parse-error handling.
        var vid = PolicyVersionId.Create(Ids);
        return new PolicyStatement(PolicyStatementId.Create(Ids), vid, true, PolicyEffect.Allow,
            new[] { PolicySubject.InteractiveUser }, Selector.ForId(sourceId).ToJson(), Selector.ForId(capId).ToJson(),
            RiskLevel.Low, RiskLevel.Low, null,
            new[] { new PolicyCondition(PolicyConditionKind.Unknown, "{}") }, 0);
    }

    // ---- context / capability builders ----

    private static EvaluationContext Ctx(
        PolicySubject subject = PolicySubject.InteractiveUser,
        string sourceId = "src_a",
        bool sourceEnabled = true,
        bool quarantined = false,
        bool spaceLinked = true,
        string? spaceId = "space_1",
        bool expertGranted = false,
        bool emergency = false,
        bool grantPresent = false,
        DateTimeOffset now = default,
        string runMode = "interactive",
        string triggerType = "manual",
        int targetCount = 1,
        long resultSize = 0,
        string sourceHealth = "healthy")
        => new(subject, sourceId, "sch_cap", sourceEnabled, quarantined, spaceLinked, spaceId,
            expertGranted, emergency, 1, grantPresent, now == default ? Now : now,
            runMode, triggerType, targetCount, resultSize, sourceHealth);

    private static CapabilityDescriptor Cap(string id, RiskLevel local, ResourceScope? manifest = null, string schema = "sch_cap")
        => new(id, schema, local, manifest);

    private static PolicySnapshot Snap(params (PolicyLayer Layer, IReadOnlyList<PolicyStatement> Stmts)[] layers)
        => PolicySnapshot.FromLayers("h", layers);

    private static readonly IReadOnlyList<PolicyLayer> PresentAll =
        new[] { PolicyLayer.BuiltInSafety, PolicyLayer.GlobalPolicy, PolicyLayer.ExpertPolicy, PolicyLayer.AutomationPolicy };

    // ---- golden vectors ----

    [Fact]
    public void Critical_capability_is_blocked_by_BuiltIn_deny()
    {
        var snap = Snap((PolicyLayer.BuiltInSafety, new[] { DenyAll(RiskLevel.Critical, RiskLevel.Critical) }));
        var d = PolicyEvaluator.Evaluate(snap, Ctx(), Cap("cap_x", RiskLevel.Critical), EvaluationArguments.Empty, PresentAll);
        Assert.Equal(PermissionDecisionKind.Deny, d.Kind);
        Assert.Equal(PolicyReasonCodes.ExplicitDeny, d.PrimaryReasonCode);
    }

    [Fact]
    public void Explicit_deny_overrides_allow_at_same_layer()
    {
        var allow = Allow("src_a", "cap_x");
        var deny = DenyAll();
        var snap = Snap((PolicyLayer.GlobalPolicy, new[] { allow, deny }));
        var d = PolicyEvaluator.Evaluate(snap, Ctx(), Cap("cap_x", RiskLevel.Low), EvaluationArguments.Empty, PresentAll);
        Assert.Equal(PermissionDecisionKind.Deny, d.Kind);
        Assert.Equal(PolicyReasonCodes.ExplicitDeny, d.PrimaryReasonCode);
    }

    [Fact]
    public void Automation_low_with_full_coverage_and_grant_is_allowed()
    {
        var snap = Snap(
            (PolicyLayer.BuiltInSafety, new[] { DenyAll(RiskLevel.Critical, RiskLevel.Critical) }),
            (PolicyLayer.GlobalPolicy, new[] { AllowAuto("src_a", "cap_x") }),
            (PolicyLayer.ExpertPolicy, new[] { AllowAuto("src_a", "cap_x") }),
            (PolicyLayer.AutomationPolicy, new[] { AllowAuto("src_a", "cap_x") }));
        var d = PolicyEvaluator.Evaluate(snap, Ctx(PolicySubject.AutomationPrincipal, grantPresent: true),
            Cap("cap_x", RiskLevel.Low), EvaluationArguments.Empty, PresentAll);
        Assert.Equal(PermissionDecisionKind.Allow, d.Kind);
    }

    [Fact]
    public void Interactive_missing_expert_automation_coverage_asks()
    {
        var snap = Snap((PolicyLayer.GlobalPolicy, new[] { Allow("src_a", "cap_x") }));
        var d = PolicyEvaluator.Evaluate(snap, Ctx(), Cap("cap_x", RiskLevel.Low), EvaluationArguments.Empty, PresentAll);
        Assert.Equal(PermissionDecisionKind.Ask, d.Kind);
    }

    [Fact]
    public void Automation_missing_expert_automation_coverage_is_denied()
    {
        var snap = Snap((PolicyLayer.GlobalPolicy, new[] { Allow("src_a", "cap_x") }));
        var d = PolicyEvaluator.Evaluate(snap, Ctx(PolicySubject.AutomationPrincipal),
            Cap("cap_x", RiskLevel.Low), EvaluationArguments.Empty, PresentAll);
        Assert.Equal(PermissionDecisionKind.Deny, d.Kind);
    }

    [Fact]
    public void Empty_policy_never_allows_fail_closed()
    {
        var snap = Snap(); // no statements at all
        var interactive = PolicyEvaluator.Evaluate(snap, Ctx(), Cap("cap_x", RiskLevel.Low), EvaluationArguments.Empty, PresentAll);
        var automation = PolicyEvaluator.Evaluate(snap, Ctx(PolicySubject.AutomationPrincipal), Cap("cap_x", RiskLevel.Low), EvaluationArguments.Empty, PresentAll);
        Assert.Equal(PermissionDecisionKind.Ask, interactive.Kind);   // interactive -> ask
        Assert.Equal(PermissionDecisionKind.Deny, automation.Kind);  // automation -> deny
    }

    [Fact]
    public void Emergency_stop_denies_everything()
    {
        var snap = Snap((PolicyLayer.GlobalPolicy, new[] { Allow("src_a", "cap_x") }));
        var d = PolicyEvaluator.Evaluate(snap, Ctx(emergency: true), Cap("cap_x", RiskLevel.Low), EvaluationArguments.Empty, PresentAll);
        Assert.Equal(PermissionDecisionKind.Deny, d.Kind);
        Assert.Equal(PolicyReasonCodes.EmergencyStopActive, d.PrimaryReasonCode);
    }

    [Fact]
    public void Disabled_source_is_denied()
    {
        var snap = Snap((PolicyLayer.GlobalPolicy, new[] { Allow("src_a", "cap_x") }));
        var d = PolicyEvaluator.Evaluate(snap, Ctx(sourceEnabled: false), Cap("cap_x", RiskLevel.Low), EvaluationArguments.Empty, PresentAll);
        Assert.Equal(PermissionDecisionKind.Deny, d.Kind);
        Assert.Equal(PolicyReasonCodes.SourceDisabled, d.PrimaryReasonCode);
    }

    // ---- scope ----

    [Fact]
    public void Scope_intersection_allows_when_invocation_within_statement_scope()
    {
        var stmtScope = new LocalProjectScope("projA", new[] { "/src" }, new[] { "read" });
        var invokeScope = new LocalProjectScope("projA", new[] { "/src", "/docs" }, new[] { "read", "write" });
        var snap = FullAllow("cap_x", "src_a", stmtScope);
        var d = PolicyEvaluator.Evaluate(snap, Ctx(PolicySubject.AutomationPrincipal, grantPresent: true),
            Cap("cap_x", RiskLevel.Low, invokeScope),
            new EvaluationArguments(invokeScope, RiskLevel.Low), PresentAll);
        Assert.Equal(PermissionDecisionKind.Allow, d.Kind);
        Assert.NotNull(d.EffectiveScope);
    }

    [Fact]
    public void Scope_disjoint_is_denied()
    {
        var stmtScope = new LocalProjectScope("projA", new[] { "/src" }, new[] { "read" });
        var invokeScope = new LocalProjectScope("projB", new[] { "/src" }, new[] { "read" });
        var snap = FullAllow("cap_x", "src_a", stmtScope);
        var d = PolicyEvaluator.Evaluate(snap, Ctx(PolicySubject.AutomationPrincipal, grantPresent: true),
            Cap("cap_x", RiskLevel.Low, invokeScope),
            new EvaluationArguments(invokeScope, RiskLevel.Low), PresentAll);
        Assert.Equal(PermissionDecisionKind.Deny, d.Kind);
        Assert.Equal(PolicyReasonCodes.ResourceOutOfScope, d.PrimaryReasonCode);
    }

    // ---- risk ----

    [Fact]
    public void Automation_medium_requires_grant_matrix()
    {
        // A Medium capability matched by a [Medium,Medium] statement yields effective risk Medium.
        // Automation needs an AutomationGrant for Medium; without it the decision is Ask, with it Allow.
        var snap = FullAllow("cap_x", "src_a", min: RiskLevel.Medium, max: RiskLevel.Medium);
        var noGrant = PolicyEvaluator.Evaluate(snap, Ctx(PolicySubject.AutomationPrincipal, grantPresent: false),
            Cap("cap_x", RiskLevel.Medium), EvaluationArguments.Empty, PresentAll);
        var withGrant = PolicyEvaluator.Evaluate(snap, Ctx(PolicySubject.AutomationPrincipal, grantPresent: true),
            Cap("cap_x", RiskLevel.Medium), EvaluationArguments.Empty, PresentAll);
        Assert.Equal(PermissionDecisionKind.Ask, noGrant.Kind);
        Assert.Equal(PermissionDecisionKind.Allow, withGrant.Kind);
        Assert.Equal(RiskLevel.Medium, withGrant.EffectiveRisk);
    }

    // ---- conditions / temporal defer ----

    [Fact]
    public void Time_window_gated_allow_defers_when_outside_window()
    {
        var cond = new PolicyCondition(PolicyConditionKind.TimeWindow,
            "{\"tz\":\"UTC\",\"from\":\"09:00\",\"to\":\"17:00\"}");
        var snap = FullAllow("cap_x", "src_a", conditions: new[] { cond });
        var now = new DateTimeOffset(2026, 7, 22, 20, 0, 0, TimeSpan.Zero); // outside 09-17 UTC
        var d = PolicyEvaluator.Evaluate(snap, Ctx(PolicySubject.AutomationPrincipal, grantPresent: true, now: now),
            Cap("cap_x", RiskLevel.Low), EvaluationArguments.Empty, PresentAll);
        Assert.Equal(PermissionDecisionKind.Defer, d.Kind);
        Assert.Equal(PolicyReasonCodes.TimeWindowDeferred, d.PrimaryReasonCode);
        Assert.NotNull(d.DeferUntilUtc);
        Assert.True(d.DeferUntilUtc > now);
    }

    [Fact]
    public void Unknown_condition_fails_closed_to_deny()
    {
        var snap = Snap((PolicyLayer.GlobalPolicy, new[] { RawAllowWithUnknownCondition("src_a", "cap_x") }));
        var d = PolicyEvaluator.Evaluate(snap, Ctx(), Cap("cap_x", RiskLevel.Low), EvaluationArguments.Empty, PresentAll);
        Assert.Equal(PermissionDecisionKind.Deny, d.Kind);
        Assert.Equal(PolicyReasonCodes.ArgumentsInvalid, d.PrimaryReasonCode);
    }

    [Fact]
    public void Wildcard_allow_statement_never_grants()
    {
        // A wildcard Allow must be excluded by the evaluator (HasWildcardAllow guard).
        var wildcard = PolicyStatement.Create(Ids, PolicyVersionId.Create(Ids), true, PolicyEffect.Allow,
            new[] { PolicySubject.InteractiveUser }, Selector.MatchAllJson, Selector.MatchAllJson,
            RiskLevel.Low, RiskLevel.Low, null, Array.Empty<PolicyCondition>(), 0);
        var snap = Snap((PolicyLayer.GlobalPolicy, new[] { wildcard }));
        var interactive = PolicyEvaluator.Evaluate(snap, Ctx(), Cap("cap_x", RiskLevel.Low), EvaluationArguments.Empty, PresentAll);
        Assert.NotEqual(PermissionDecisionKind.Allow, interactive.Kind);
    }

    // ---- simulator shares the real evaluator ----

    [Fact]
    public void Simulator_produces_identical_decision_to_evaluator()
    {
        var snap = Snap(
            (PolicyLayer.BuiltInSafety, new[] { DenyAll(RiskLevel.Critical, RiskLevel.Critical) }),
            (PolicyLayer.GlobalPolicy, new[] { Allow("src_a", "cap_x") }),
            (PolicyLayer.ExpertPolicy, new[] { Allow("src_a", "cap_x") }),
            (PolicyLayer.AutomationPolicy, new[] { Allow("src_a", "cap_x") }));
        var ctx = Ctx(PolicySubject.AutomationPrincipal, grantPresent: true);
        var cap = Cap("cap_x", RiskLevel.Low);
        var real = PolicyEvaluator.Evaluate(snap, ctx, cap, EvaluationArguments.Empty, PresentAll);
        var sim = PolicySimulator.Simulate(snap, ctx, cap, EvaluationArguments.Empty, PresentAll);
        Assert.Equal(real.Kind, sim.Kind);
        Assert.Equal(real.PrimaryReasonCode, sim.PrimaryReasonCode);
        Assert.Equal(real.StableTrace(), sim.StableTrace());
    }

    // ---- helper: full-coverage allow snapshot ----

    private static PolicySnapshot FullAllow(
        string capId, string sourceId, ResourceScope? scope = null,
        RiskLevel min = RiskLevel.Low, RiskLevel max = RiskLevel.Low,
        IReadOnlyList<PolicyCondition>? conditions = null)
    {
        var g = Allow(sourceId, capId, min, max, scope, conditions, PolicySubject.AutomationPrincipal, PolicyLayer.GlobalPolicy);
        var e = Allow(sourceId, capId, min, max, scope, conditions, PolicySubject.AutomationPrincipal, PolicyLayer.ExpertPolicy);
        var a = Allow(sourceId, capId, min, max, scope, conditions, PolicySubject.AutomationPrincipal, PolicyLayer.AutomationPolicy);
        return Snap(
            (PolicyLayer.BuiltInSafety, new[] { DenyAll(RiskLevel.Critical, RiskLevel.Critical) }),
            (PolicyLayer.GlobalPolicy, new[] { g }),
            (PolicyLayer.ExpertPolicy, new[] { e }),
            (PolicyLayer.AutomationPolicy, new[] { a }));
    }
}
