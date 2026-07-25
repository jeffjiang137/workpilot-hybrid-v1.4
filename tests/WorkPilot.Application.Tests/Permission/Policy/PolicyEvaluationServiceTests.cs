using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Application.Permission.Policy;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.PermissionGovernance;
using WorkPilot.Domain.PermissionGovernance.Evaluation;
using Xunit;

namespace WorkPilot.Application.Tests.Permission.Policy;

public class PolicyEvaluationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly SequentialIdGenerator Ids = new();

    private static PolicyStatement BuiltInCriticalDeny()
    {
        var vid = PolicyVersionId.Create(Ids);
        return PolicyStatement.Create(Ids, vid, true, PolicyEffect.Deny,
            new[] { PolicySubject.InteractiveUser, PolicySubject.AutomationPrincipal, PolicySubject.SystemMaintenance },
            Selector.MatchAllJson, Selector.MatchAllJson, RiskLevel.Critical, RiskLevel.Critical, null,
            Array.Empty<PolicyCondition>(), 0);
    }

    private static PolicyStatement GlobalAllowLow(string sourceId, string capId)
    {
        var vid = PolicyVersionId.Create(Ids);
        return PolicyStatement.Create(Ids, vid, true, PolicyEffect.Allow,
            new[] { PolicySubject.InteractiveUser }, Selector.ForId(sourceId).ToJson(), Selector.ForId(capId).ToJson(),
            RiskLevel.Low, RiskLevel.Low, null, Array.Empty<PolicyCondition>(), 0);
    }

    private static EvaluationContext Ctx(PolicySubject subject = PolicySubject.InteractiveUser, bool emergency = false, bool sourceEnabled = true)
        => new(subject, "src_a", "sch_cap", sourceEnabled, false, true, "space_1",
            false, emergency, 1, false, Now, "interactive", "manual", 1, 0, "healthy");

    private static CapabilityDescriptor Cap(string id, RiskLevel local) => new(id, "sch_cap", local, null);

    private static CurrentPolicyBundle Bundle(PolicyLayer layer, params PolicyStatement[] stmts)
    {
        var vid = PolicyVersionId.Create(Ids);
        var doc = new PolicyDocument(PolicyDocumentId.Create(Ids), layer, null, vid, Now, Now);
        var ver = new PolicyVersion(vid, doc.Id, 1, "h", "[]", true, Now);
        return new CurrentPolicyBundle(doc, ver, stmts);
    }

    private sealed class FakePolicyStore : IPolicyStore
    {
        private readonly Dictionary<(PolicyLayer, string?), CurrentPolicyBundle> _bundles = new();
        public void Seed(PolicyLayer layer, params PolicyStatement[] stmts) => _bundles[(layer, null)] = Bundle(layer, stmts);

        public Task<Result<CurrentPolicyBundle>> GetCurrentAsync(PolicyLayer layer, string? scopeId, CancellationToken ct = default)
            => _bundles.TryGetValue((layer, scopeId), out var b)
                ? Task.FromResult(Result<CurrentPolicyBundle>.Ok(b))
                : Task.FromResult(Result<CurrentPolicyBundle>.Fail(PolicyErrors.NotFoundError()));

        public Task<Result<PolicyVersionId>> SaveNewVersionAsync(PolicyDocumentId documentId, IReadOnlyList<PolicyStatement> statements, string actor, string reasonCode, IClock clock, CancellationToken ct = default)
            => Task.FromResult(Result<PolicyVersionId>.Fail(PolicyErrors.NotFoundError()));
        public Task<Result<PolicyVersionId>> RecoverDefaultAsync(PolicyLayer layer, string? scopeId, IClock clock, CancellationToken ct = default)
            => Task.FromResult(Result<PolicyVersionId>.Fail(PolicyErrors.NotFoundError()));
        public Task<Result<PolicyAuditPage>> ListAuditAsync(int limit, int offset, CancellationToken ct = default)
            => Task.FromResult(Result<PolicyAuditPage>.Fail(PolicyErrors.NotFoundError()));
        public Task<Result<bool>> VerifyIntegrityAsync(CancellationToken ct = default)
            => Task.FromResult(Result<bool>.Ok(true));
        public Task InvalidateReceiptsForPolicyHashAsync(string policyHash, IClock clock, CancellationToken ct = default) => Task.CompletedTask;
        public Task EnsureDefaultPolicyAsync(IClock clock, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task Critical_capability_is_denied_through_service()
    {
        var store = new FakePolicyStore();
        store.Seed(PolicyLayer.BuiltInSafety, BuiltInCriticalDeny());
        var svc = new PolicyEvaluationService(store, new PolicyEvaluationCache());
        var d = await svc.EvaluateAsync(Ctx(), Cap("cap_x", RiskLevel.Critical), EvaluationArguments.Empty);
        Assert.Equal(PermissionDecisionKind.Deny, d.Kind);
        Assert.False(string.IsNullOrEmpty(d.PolicyHash));
        Assert.NotEmpty(d.Trace);
    }

    [Fact]
    public async Task Default_minimum_policy_never_allows_fail_closed()
    {
        var store = new FakePolicyStore();
        store.Seed(PolicyLayer.BuiltInSafety, BuiltInCriticalDeny());
        // GlobalPolicy intentionally not seeded (no allowlist)
        var svc = new PolicyEvaluationService(store, new PolicyEvaluationCache());
        var interactive = await svc.EvaluateAsync(Ctx(), Cap("cap_x", RiskLevel.Low), EvaluationArguments.Empty);
        var automation = await svc.EvaluateAsync(Ctx(PolicySubject.AutomationPrincipal), Cap("cap_x", RiskLevel.Low), EvaluationArguments.Empty);
        Assert.Equal(PermissionDecisionKind.Ask, interactive.Kind);
        Assert.Equal(PermissionDecisionKind.Deny, automation.Kind);
    }

    [Fact]
    public async Task Decision_is_cached_and_reused()
    {
        var store = new FakePolicyStore();
        store.Seed(PolicyLayer.BuiltInSafety, BuiltInCriticalDeny());
        var svc = new PolicyEvaluationService(store, new PolicyEvaluationCache());
        var d1 = await svc.EvaluateAsync(Ctx(), Cap("cap_x", RiskLevel.Critical), EvaluationArguments.Empty);
        var d2 = await svc.EvaluateAsync(Ctx(), Cap("cap_x", RiskLevel.Critical), EvaluationArguments.Empty);
        Assert.Same(d1, d2); // served from cache
    }

    [Fact]
    public async Task Policy_content_change_produces_fresh_decision()
    {
        var store = new FakePolicyStore();
        store.Seed(PolicyLayer.BuiltInSafety, BuiltInCriticalDeny());
        var svc = new PolicyEvaluationService(store, new PolicyEvaluationCache());

        var before = await svc.EvaluateAsync(Ctx(), Cap("cap_x", RiskLevel.Critical), EvaluationArguments.Empty);
        Assert.Equal(PermissionDecisionKind.Deny, before.Kind);

        // Mutate the policy (remove the BuiltIn deny). The cache key includes the policy hash, so this
        // is a cache miss; we also broadcast InvalidateCache for safety (doc 07 §13).
        store.Seed(PolicyLayer.BuiltInSafety); // empty BuiltIn document
        svc.InvalidateCache();
        var after = await svc.EvaluateAsync(Ctx(), Cap("cap_x", RiskLevel.Critical), EvaluationArguments.Empty);
        Assert.Equal(PermissionDecisionKind.Ask, after.Kind); // no longer denied
        Assert.NotEqual(before.Kind, after.Kind);
    }

    [Fact]
    public async Task Native_final_gate_returns_fresh_decision_without_permit_in_managed_mode()
    {
        var store = new FakePolicyStore();
        store.Seed(PolicyLayer.BuiltInSafety, BuiltInCriticalDeny());
        var gate = new ManagedPolicyGate(store);
        var result = await gate.CheckAsync(Ctx(), Cap("cap_x", RiskLevel.Critical), EvaluationArguments.Empty);
        Assert.Equal(PermissionDecisionKind.Deny, result.Kind);
        Assert.Null(result.PermitToken); // managed stand-in cannot issue a native permit
        Assert.False(string.IsNullOrEmpty(result.PolicyHash));
    }
}
