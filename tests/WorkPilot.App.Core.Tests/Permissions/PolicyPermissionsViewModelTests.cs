using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.App.Core.Permissions;
using WorkPilot.Application.Permission.Policy;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.PermissionGovernance;
using WorkPilot.Domain.PermissionGovernance.Evaluation;
using Xunit;
using WorkPilot.App.Core.Tests.Fakes;

namespace WorkPilot.App.Core.Tests.Permissions;

/// <summary>BCL view-model tests for the permission page (PER-003/004/008–010, T18f). The view-model
/// keeps all governance logic in the Application services; these tests drive it through its public
/// commands/methods with in-memory fakes (no SQLite, no WinUI).</summary>
public class PolicyPermissionsViewModelTests
{
    private sealed class FakePolicyStore : IPolicyStore
    {
        private readonly IIdGenerator _ids;
        private readonly IClock _clock;
        public int SaveCount;
        public FakePolicyStore(IIdGenerator ids, IClock clock) { _ids = ids; _clock = clock; }
        public Task<Result<PolicyVersionId>> SaveNewVersionAsync(PolicyDocumentId documentId, IReadOnlyList<PolicyStatement> statements, string actor, string reasonCode, IClock clock, CancellationToken ct = default)
        { SaveCount++; return Task.FromResult(Result<PolicyVersionId>.Ok(PolicyVersionId.Create(_ids))); }
        public Task<Result<PolicyVersionId>> RecoverDefaultAsync(PolicyLayer layer, string? scopeId, IClock clock, CancellationToken ct = default)
            => Task.FromResult(Result<PolicyVersionId>.Ok(PolicyVersionId.Create(_ids)));
        public Task<Result<CurrentPolicyBundle>> GetCurrentAsync(PolicyLayer layer, string? scopeId, CancellationToken ct = default)
        {
            var (doc, version, statements) = DefaultPolicyProvider.BuildDefault(layer, scopeId, _ids, _clock);
            return Task.FromResult(Result<CurrentPolicyBundle>.Ok(new CurrentPolicyBundle(doc, version, statements)));
        }
        public Task<Result<PolicyAuditPage>> ListAuditAsync(int limit, int offset, CancellationToken ct = default)
            => Task.FromResult(Result<PolicyAuditPage>.Ok(new PolicyAuditPage(Array.Empty<PolicyAuditRecord>(), 0)));
        public Task<Result<bool>> VerifyIntegrityAsync(CancellationToken ct = default)
            => Task.FromResult(Result<bool>.Ok(true));
        public Task InvalidateReceiptsForPolicyHashAsync(string policyHash, IClock clock, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task EnsureDefaultPolicyAsync(IClock clock, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeGrantStore : IGrantStore
    {
        public List<PolicyGrant> Active = new();
        public int RevokeCount;
        public Task<Result<PolicyGrantId>> IssueAsync(PolicyGrant grant, IClock clock, CancellationToken ct = default)
            => Task.FromResult(Result<PolicyGrantId>.Ok(grant.GrantId));
        public Task<Result<PolicyGrant>> GetAsync(PolicyGrantId id, CancellationToken ct = default)
            => Task.FromResult(Result<PolicyGrant>.Fail(PolicyErrors.GrantNotFoundError(id.Value)));
        public Task<Result<IReadOnlyList<PolicyGrant>>> ListByAutomationAsync(string automationId, string revisionId, CancellationToken ct = default)
            => Task.FromResult(Result<IReadOnlyList<PolicyGrant>>.Ok(Array.Empty<PolicyGrant>()));
        public Task<Result<IReadOnlyList<PolicyGrant>>> ListActiveGrantsAsync(string capabilityStableId, string sourceKind, string sourceId, string schemaSha256, DateTimeOffset nowUtc, CancellationToken ct = default)
            => Task.FromResult(Result<IReadOnlyList<PolicyGrant>>.Ok(Active.ToList()));
        public Task<Result<IReadOnlyList<PolicyGrant>>> ListActiveAsync(DateTimeOffset nowUtc, CancellationToken ct = default)
            => Task.FromResult(Result<IReadOnlyList<PolicyGrant>>.Ok(Active.Where(g => g.RevokedAtUtc is null && g.ExpiresAtUtc > nowUtc).ToList()));
        public Task<Result<PolicyGrant>> RevokeAsync(PolicyGrantId id, IClock clock, CancellationToken ct = default)
        {
            RevokeCount++;
            var idx = Active.FindIndex(x => x.GrantId.Value == id.Value);
            if (idx < 0) return Task.FromResult(Result<PolicyGrant>.Fail(PolicyErrors.GrantNotFoundError(id.Value)));
            var revoked = Active[idx].Revoke(clock.UtcNow);
            Active[idx] = revoked;
            return Task.FromResult(Result<PolicyGrant>.Ok(revoked));
        }
    }

    private sealed class FakeImpactAnalyzer : IPolicyImpactAnalyzer
    {
        public bool Expansion;
        public int AffectedGrants;
        public Task<Result<PolicyImpactReport>> AnalyzeAsync(PolicySnapshot oldSnapshot, PolicySnapshot newSnapshot, IReadOnlyList<ImpactTarget> targets, DateTimeOffset nowUtc, IReadOnlyList<PolicyLayer>? relevantLayers = null, int affectedGrantCount = 0, int queuedRunCount = 0, CancellationToken ct = default)
            => Task.FromResult(Result<PolicyImpactReport>.Ok(new PolicyImpactReport(
                new StatementDiff(Array.Empty<PolicyStatementId>(), Array.Empty<PolicyStatementId>(), Array.Empty<PolicyStatementId>()),
                Array.Empty<TargetImpact>(),
                Expansion, Expansion, AffectedGrants, queuedRunCount, 0)));
    }

    private sealed class FakeEpoch : IRevocationEpoch
    {
        public long Current { get; private set; }
        public int Bumps;
        public void Bump() { Current++; Bumps++; }
    }

    private static (PolicyPermissionsViewModel Vm, FakePolicyStore Store, FakeGrantStore Grants, FakeImpactAnalyzer Impact, FakeEpoch Epoch) Build()
    {
        var ids = new SeqIdGenerator();
        var clock = new StubClock();
        var store = new FakePolicyStore(ids, clock);
        var simulator = new PolicySimulatorService(store);
        var projection = new PolicyProjectionService(simulator);
        var impact = new FakeImpactAnalyzer();
        var epoch = new FakeEpoch();
        var admin = new PolicyAdminService(store, impact, epoch);
        var grants = new FakeGrantStore();
        var vm = new PolicyPermissionsViewModel(store, projection, admin, grants, clock);
        return (vm, store, grants, impact, epoch);
    }

    private static PolicyGrant MakeGrant(string capId, string sourceKind, string sourceId, StubClock clock, IIdGenerator ids)
    {
        var req = new GrantIssueRequest(
            "auto-1", "rev-1", null, null, sourceKind, sourceId, capId, "sch",
            new LocalProjectScope("p", Array.Empty<string>(), Array.Empty<string>()),
            RiskLevel.Medium, clock.UtcNow, clock.UtcNow + TimeSpan.FromDays(1));
        return PolicyGrant.Create(ids, req, clock.UtcNow, 0);
    }

    private static (PolicyStatement Statement, ImpactTarget Target) MakeEdit(string capId, PolicyEffect effect, RiskLevel risk)
    {
        var ids = new SeqIdGenerator();
        var versionId = PolicyVersionId.Create(ids);
        var stmt = PolicyStatement.Create(
            ids, versionId, true, effect,
            new[] { PolicySubject.AutomationPrincipal },
            "{\"source\":\"mcp:src-1\"}", $"{{\"capability\":\"{capId}\"}}",
            RiskLevel.Low, risk, null, Array.Empty<PolicyCondition>(), 0);
        var target = new ImpactTarget(
            "auto-1", "mcp", "src-1", string.Empty, capId, string.Empty, risk, null, "space-1");
        return (stmt, target);
    }

    [Fact]
    public async Task LoadGrantsAsync_populates_active_grants_and_clears_error()
    {
        var (vm, _, grants, _, _) = Build();
        grants.Active.Add(MakeGrant("cap-1", "mcp", "src-1", new StubClock(), new SeqIdGenerator()));

        await vm.LoadGrantsAsync();

        Assert.Single(vm.ActiveGrants);
        Assert.Null(vm.LastError);
    }

    [Fact]
    public async Task ProjectAsync_returns_fail_closed_decision_for_unallowed_capability()
    {
        var (vm, _, _, _, _) = Build();
        var ctx = new EvaluationContext(
            PolicySubject.AutomationPrincipal, "src-1", "sch-src", true, false, true, "space-1",
            true, false, 1, false, DateTimeOffset.UnixEpoch, "interactive", "manual", 1, 0, "healthy");
        var queries = new[] { new CapabilityQuery("cap-1", "sch-src", RiskLevel.Low, null) };

        await vm.ProjectAsync(ctx, queries);

        Assert.Single(vm.EffectivePermissions);
        Assert.Equal("cap-1", vm.EffectivePermissions[0].CapabilityStableId);
        Assert.Equal(PermissionDecisionKind.Deny, vm.EffectivePermissions[0].Decision);
    }

    [Fact]
    public async Task PrepareSaveAsync_flags_confirmation_when_impact_reports_expansion()
    {
        var (vm, _, _, impact, _) = Build();
        impact.Expansion = true; impact.AffectedGrants = 3;
        var (stmt, target) = MakeEdit("cap-x", PolicyEffect.Allow, RiskLevel.Medium);

        var report = await vm.PrepareSaveAsync(PolicyLayer.SpacePolicy, "space-1", new[] { stmt }, new[] { target }, "user");

        Assert.NotNull(report);
        Assert.True(vm.HasPendingImpact);
        Assert.True(vm.RequiresConfirmation);
        Assert.Equal(3, report!.AffectedGrantCount);
    }

    [Fact]
    public async Task ConfirmAndSaveAsync_commits_when_pending_edit_exists_and_bumps_epoch()
    {
        var (vm, store, _, impact, epoch) = Build();
        impact.Expansion = true;
        var (stmt, target) = MakeEdit("cap-x", PolicyEffect.Allow, RiskLevel.Medium);

        await vm.PrepareSaveAsync(PolicyLayer.SpacePolicy, "space-1", new[] { stmt }, new[] { target }, "user");
        var ok = await vm.ConfirmAndSaveAsync();

        Assert.True(ok);
        Assert.Equal(1, store.SaveCount);
        Assert.Equal(1, epoch.Bumps);
        Assert.False(vm.HasPendingImpact);
        Assert.False(vm.RequiresConfirmation);
    }

    [Fact]
    public async Task ConfirmAndSaveAsync_returns_false_without_prior_preview()
    {
        var (vm, store, _, _, _) = Build();

        var ok = await vm.ConfirmAndSaveAsync();

        Assert.False(ok);
        Assert.Equal(0, store.SaveCount);
        Assert.NotNull(vm.LastError);
    }

    [Fact]
    public async Task RevokeGrantAsync_invokes_store_and_reloads()
    {
        var (vm, _, grants, _, _) = Build();
        var clock = new StubClock();
        var grant = MakeGrant("cap-1", "mcp", "src-1", clock, new SeqIdGenerator());
        grants.Active.Add(grant);

        var ok = await vm.RevokeGrantAsync(grant.GrantId);

        Assert.True(ok);
        Assert.Equal(1, grants.RevokeCount);
    }
}
