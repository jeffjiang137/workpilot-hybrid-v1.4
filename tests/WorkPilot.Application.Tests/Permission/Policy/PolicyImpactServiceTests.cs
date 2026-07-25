using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Application.Permission.Policy;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.PermissionGovernance;
using WorkPilot.Domain.PermissionGovernance.Evaluation;
using Xunit;

namespace WorkPilot.Application.Tests.Permission.Policy;

public class PolicyImpactServiceTests
{
    private static readonly SequentialIdGenerator Ids = new();
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

    private static LayeredStatement DenyAll()
    {
        var vid = PolicyVersionId.Create(Ids);
        var st = PolicyStatement.Create(Ids, vid, true, PolicyEffect.Deny,
            new[] { PolicySubject.InteractiveUser, PolicySubject.AutomationPrincipal, PolicySubject.SystemMaintenance },
            Selector.MatchAllJson, Selector.MatchAllJson, RiskLevel.Low, RiskLevel.Critical, null, Array.Empty<PolicyCondition>(), 0);
        return new LayeredStatement(PolicyLayer.BuiltInSafety, st);
    }

    private static LayeredStatement AllowAuto(string capId, string srcId, PolicyLayer layer)
    {
        var vid = PolicyVersionId.Create(Ids);
        var st = PolicyStatement.Create(Ids, vid, true, PolicyEffect.Allow,
            new[] { PolicySubject.AutomationPrincipal },
            Selector.ForId(srcId).ToJson(), Selector.ForId(capId).ToJson(),
            RiskLevel.Low, RiskLevel.Low, null, Array.Empty<PolicyCondition>(), 0);
        return new LayeredStatement(layer, st);
    }

    private static PolicySnapshot Snap(params LayeredStatement[] s) => new("hash", s);

    private static ImpactTarget Target(string capId = "cap-1", string srcId = "src-1")
        => new("auto-1", "mcp", srcId, "sch-src", capId, "sch-cap", RiskLevel.Low, null, "space-1");

    [Fact]
    public async Task Expansion_counts_affected_active_grants()
    {
        var oldS = Snap(DenyAll());
        var newS = Snap(AllowAuto("cap-1", "src-1", PolicyLayer.ExpertPolicy), AllowAuto("cap-1", "src-1", PolicyLayer.AutomationPolicy));

        var store = new StubGrantStore(activeForCap: "cap-1", count: 2);
        var svc = new PolicyImpactService(store);

        var r = await svc.AnalyzeAsync(oldS, newS, new[] { Target() }, Now, queuedRunCount: 3, pendingApprovalCount: 1, ct: CancellationToken.None);

        Assert.True(r.IsSuccess);
        Assert.True(r.Value!.HasPrivilegeExpansion);
        Assert.Equal(2, r.Value.AffectedGrantCount);
        Assert.Equal(3, r.Value.QueuedRunCount);
        Assert.Equal(1, r.Value.PendingApprovalCount);
    }

    [Fact]
    public async Task Saving_blocked_when_target_set_exceeds_cap()
    {
        var oldS = Snap(DenyAll());
        var newS = Snap(AllowAuto("cap-1", "src-1", PolicyLayer.ExpertPolicy), AllowAuto("cap-1", "src-1", PolicyLayer.AutomationPolicy));

        var targets = Enumerable.Range(0, Limits.V1_5.MaxImpactAnalysisTargets + 1)
            .Select(i => Target(capId: $"cap-{i}", srcId: $"src-{i}")).ToList();

        var svc = new PolicyImpactService();
        var r = await svc.AnalyzeAsync(oldS, newS, targets, Now, ct: CancellationToken.None);

        Assert.False(r.IsSuccess);
        Assert.Equal("POLICY_IMPACT_INCOMPLETE", r.Error!.Code);
    }

    private sealed class StubGrantStore : IGrantStore
    {
        private readonly string _activeForCap;
        private readonly int _count;
        public StubGrantStore(string activeForCap, int count) { _activeForCap = activeForCap; _count = count; }

        public Task<Result<PolicyGrantId>> IssueAsync(PolicyGrant grant, IClock clock, CancellationToken ct = default)
            => Task.FromResult(Result<PolicyGrantId>.Ok(grant.GrantId));
        public Task<Result<PolicyGrant>> GetAsync(PolicyGrantId id, CancellationToken ct = default)
            => Task.FromResult(Result<PolicyGrant>.Fail(PolicyErrors.GrantNotFoundError(id.Value)));
        public Task<Result<IReadOnlyList<PolicyGrant>>> ListByAutomationAsync(string automationId, string revisionId, CancellationToken ct = default)
            => Task.FromResult(Result<IReadOnlyList<PolicyGrant>>.Ok(new List<PolicyGrant>()));
        public Task<Result<IReadOnlyList<PolicyGrant>>> ListActiveGrantsAsync(string capabilityStableId, string sourceKind, string sourceId, string schemaSha256, DateTimeOffset nowUtc, CancellationToken ct = default)
        {
            var list = capabilityStableId == _activeForCap
                ? Enumerable.Range(0, _count).Select(_ => PolicyGrant.Create(new SequentialIdGenerator(),
                    new GrantIssueRequest("a", "r", null, null, "mcp", "src-1", capabilityStableId, schemaSha256,
                        new LocalProjectScope("p", new List<string> { "x" }, new List<string> { "y" }), RiskLevel.Medium, nowUtc, nowUtc + TimeSpan.FromDays(1)),
                    nowUtc, 1)).ToList()
                : new List<PolicyGrant>();
            return Task.FromResult(Result<IReadOnlyList<PolicyGrant>>.Ok(list));
        }
        public Task<Result<IReadOnlyList<PolicyGrant>>> ListActiveAsync(DateTimeOffset nowUtc, CancellationToken ct = default)
            => Task.FromResult(Result<IReadOnlyList<PolicyGrant>>.Ok(new List<PolicyGrant>()));
        public Task<Result<PolicyGrant>> RevokeAsync(PolicyGrantId id, IClock clock, CancellationToken ct = default)
            => Task.FromResult(Result<PolicyGrant>.Fail(PolicyErrors.GrantNotFoundError(id.Value)));
    }
}
