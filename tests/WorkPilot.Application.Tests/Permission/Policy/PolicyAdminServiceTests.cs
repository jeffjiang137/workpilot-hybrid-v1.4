using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Application.Permission.Policy;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation.Run;
using WorkPilot.Domain.PermissionGovernance;
using WorkPilot.Domain.PermissionGovernance.Evaluation;
using Xunit;

namespace WorkPilot.Application.Tests.Permission.Policy;

public class PolicyAdminServiceTests
{
    private static readonly IIdGenerator Ids = new SequentialIdGenerator();
    private static readonly IClock Clock = new FakeClock(DateTimeOffset.UnixEpoch);

    private static PolicyStatement ValidAllow(IIdGenerator ids, PolicyVersionId vid) =>
        PolicyStatement.Create(ids, vid, true, PolicyEffect.Allow,
            new[] { PolicySubject.AutomationPrincipal },
            Selector.ForId("src-1").ToJson(), Selector.ForId("cap-1").ToJson(),
            RiskLevel.Low, RiskLevel.Low, null, Array.Empty<PolicyCondition>(), 0);

    private static ImpactTarget Target() =>
        new("auto-1", "mcp", "src-1", "sch-src", "cap-1", "sch-cap", RiskLevel.Low, null, "space-1");

    private static PolicyImpactReport Report(bool expansion, bool epoch, int grants = 0) =>
        new(
            new StatementDiff(Array.Empty<PolicyStatementId>(), Array.Empty<PolicyStatementId>(), Array.Empty<PolicyStatementId>()),
            Array.Empty<TargetImpact>(), expansion, epoch, grants, 0, 0);

    // ---- T18d DoD: 扩大权限二次确认 ----

    [Fact]
    public async Task Expansion_without_confirmation_is_blocked()
    {
        var store = new FakePolicyStore();
        var analyzer = new FakeImpactAnalyzer(Report(expansion: true, epoch: true, grants: 2));
        var epoch = new FakeEpoch();
        var svc = new PolicyAdminService(store, analyzer, epoch);

        var r = await svc.SavePolicyAsync(PolicyLayer.ExpertPolicy, null,
            new[] { ValidAllow(Ids, PolicyVersionId.Parse("v1")) }, new[] { Target() },
            "actor", confirmedExpansion: false, Clock, ct: CancellationToken.None);

        Assert.True(!r.IsSuccess);
        Assert.Equal("POLICY_EXPANSION_REQUIRES_CONFIRMATION", r.Error!.Code);
        Assert.Equal(0, store.SaveCalls);     // nothing persisted
        Assert.Equal(0, epoch.BumpCalls);     // epoch untouched
    }

    [Fact]
    public async Task Expansion_with_confirmation_proceeds_and_bumps_epoch()
    {
        var store = new FakePolicyStore();
        var analyzer = new FakeImpactAnalyzer(Report(expansion: true, epoch: true, grants: 2));
        var epoch = new FakeEpoch();
        var svc = new PolicyAdminService(store, analyzer, epoch);

        var r = await svc.SavePolicyAsync(PolicyLayer.ExpertPolicy, null,
            new[] { ValidAllow(Ids, PolicyVersionId.Parse("v1")) }, new[] { Target() },
            "actor", confirmedExpansion: true, Clock, ct: CancellationToken.None);

        Assert.True(r.IsSuccess);
        Assert.Equal(1, store.SaveCalls);
        Assert.Equal(1, epoch.BumpCalls);     // widening → epoch bumped
    }

    [Fact]
    public async Task No_expansion_saves_without_epoch_bump()
    {
        var store = new FakePolicyStore();
        var analyzer = new FakeImpactAnalyzer(Report(expansion: false, epoch: false));
        var epoch = new FakeEpoch();
        var svc = new PolicyAdminService(store, analyzer, epoch);

        var r = await svc.SavePolicyAsync(PolicyLayer.ExpertPolicy, null,
            new[] { ValidAllow(Ids, PolicyVersionId.Parse("v1")) }, new[] { Target() },
            "actor", confirmedExpansion: false, Clock, ct: CancellationToken.None);

        Assert.True(r.IsSuccess);
        Assert.Equal(1, store.SaveCalls);
        Assert.Equal(0, epoch.BumpCalls);     // no widening → no epoch bump
    }

    [Fact]
    public async Task Incomplete_impact_blocks_save()
    {
        var store = new FakePolicyStore();
        var analyzer = new FakeImpactAnalyzer(failCode: "POLICY_IMPACT_INCOMPLETE");
        var svc = new PolicyAdminService(store, analyzer);

        var r = await svc.SavePolicyAsync(PolicyLayer.ExpertPolicy, null,
            new[] { ValidAllow(Ids, PolicyVersionId.Parse("v1")) }, new[] { Target() },
            "actor", confirmedExpansion: false, Clock, ct: CancellationToken.None);

        Assert.True(!r.IsSuccess);
        Assert.Equal("POLICY_IMPACT_INCOMPLETE", r.Error!.Code);
        Assert.Equal(0, store.SaveCalls);
    }

    [Fact]
    public async Task Invalid_statement_blocks_before_impact()
    {
        var store = new FakePolicyStore();
        var analyzer = new FakeImpactAnalyzer(Report(expansion: false, epoch: false));
        var svc = new PolicyAdminService(store, analyzer);

        // RiskMin > RiskMax → structurally invalid (bypasses Create validation).
        var invalid = new PolicyStatement(
            PolicyStatementId.Create(Ids), PolicyVersionId.Parse("v1"), true, PolicyEffect.Allow,
            new[] { PolicySubject.AutomationPrincipal },
            Selector.MatchAllJson, Selector.MatchAllJson,
            RiskLevel.High, RiskLevel.Low, null, Array.Empty<PolicyCondition>(), 0);

        var r = await svc.SavePolicyAsync(PolicyLayer.ExpertPolicy, null,
            new[] { invalid }, new[] { Target() },
            "actor", confirmedExpansion: false, Clock, ct: CancellationToken.None);

        Assert.True(!r.IsSuccess);
        Assert.Equal("POLICY_RISK_RANGE", r.Error!.Code);
        Assert.Equal(0, analyzer.AnalyzeCalls);  // impact never reached
        Assert.Equal(0, store.SaveCalls);
    }

    [Fact]
    public async Task Document_not_found_blocks_save()
    {
        var store = new FakePolicyStore(documentExists: false);
        var analyzer = new FakeImpactAnalyzer(Report(expansion: false, epoch: false));
        var svc = new PolicyAdminService(store, analyzer);

        var r = await svc.SavePolicyAsync(PolicyLayer.ExpertPolicy, null,
            new[] { ValidAllow(Ids, PolicyVersionId.Parse("v1")) }, new[] { Target() },
            "actor", confirmedExpansion: false, Clock, ct: CancellationToken.None);

        Assert.True(!r.IsSuccess);
        Assert.Equal("POLICY_NOT_FOUND", r.Error!.Code);
        Assert.Equal(0, store.SaveCalls);
    }

    // ---- Real analyzer integration: no expansion when old==new (best-effort baseline snapshot) ----

    [Fact]
    public async Task Real_analyzer_no_expansion_saves_and_issues_no_grants()
    {
        var store = new FakePolicyStore();
        var grants = new FakeGrantStore();
        var analyzer = new PolicyImpactService(grants);   // real analyzer, grant store wired
        var svc = new PolicyAdminService(store, analyzer);

        var stmts = new[] { ValidAllow(Ids, PolicyVersionId.Parse("v1")) };
        var r = await svc.SavePolicyAsync(PolicyLayer.ExpertPolicy, null,
            stmts, new[] { Target() },
            "actor", confirmedExpansion: false, Clock, ct: CancellationToken.None);

        Assert.True(r.IsSuccess);
        Assert.Equal(1, store.SaveCalls);
        Assert.Equal(0, grants.IssueCalls);   // admin save never issues/inherits grants
    }

    // ---------------- fakes ----------------

    private sealed class FakePolicyStore : IPolicyStore
    {
        public int SaveCalls;
        private readonly bool _documentExists;
        public FakePolicyStore(bool documentExists = true) => _documentExists = documentExists;

        public Task<Result<PolicyVersionId>> SaveNewVersionAsync(
            PolicyDocumentId documentId, IReadOnlyList<PolicyStatement> statements,
            string actor, string reasonCode, IClock clock, CancellationToken ct = default)
        {
            SaveCalls++;
            return Task.FromResult(Result<PolicyVersionId>.Ok(PolicyVersionId.Parse("ver-new")));
        }

        public Task<Result<PolicyVersionId>> RecoverDefaultAsync(
            PolicyLayer layer, string? scopeId, IClock clock, CancellationToken ct = default)
            => Task.FromResult(Result<PolicyVersionId>.Ok(PolicyVersionId.Parse("ver-rec")));

        public Task<Result<CurrentPolicyBundle>> GetCurrentAsync(
            PolicyLayer layer, string? scopeId, CancellationToken ct = default)
        {
            if (!_documentExists)
                return Task.FromResult(Result<CurrentPolicyBundle>.Fail(PolicyErrors.NotFoundError()));
            var doc = new PolicyDocument(PolicyDocumentId.Parse("doc-1"), layer, scopeId,
                PolicyVersionId.Parse("ver-1"), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
            var ver = new PolicyVersion(PolicyVersionId.Parse("ver-1"), doc.Id, 1, "hash", "{}", false, DateTimeOffset.UnixEpoch);
            return Task.FromResult(Result<CurrentPolicyBundle>.Ok(new CurrentPolicyBundle(doc, ver, Array.Empty<PolicyStatement>())));
        }

        public Task<Result<PolicyAuditPage>> ListAuditAsync(int limit, int offset, CancellationToken ct = default)
            => Task.FromResult(Result<PolicyAuditPage>.Ok(new PolicyAuditPage(Array.Empty<PolicyAuditRecord>(), 0)));

        public Task<Result<bool>> VerifyIntegrityAsync(CancellationToken ct = default)
            => Task.FromResult(Result<bool>.Ok(true));

        public Task InvalidateReceiptsForPolicyHashAsync(string policyHash, IClock clock, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task EnsureDefaultPolicyAsync(IClock clock, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class FakeImpactAnalyzer : IPolicyImpactAnalyzer
    {
        private readonly PolicyImpactReport? _report;
        private readonly string? _failCode;
        public int AnalyzeCalls;
        public FakeImpactAnalyzer(PolicyImpactReport report) => _report = report;
        public FakeImpactAnalyzer(string failCode) => _failCode = failCode;

        public Task<Result<PolicyImpactReport>> AnalyzeAsync(
            PolicySnapshot oldSnapshot, PolicySnapshot newSnapshot, IReadOnlyList<ImpactTarget> targets,
            DateTimeOffset nowUtc, IReadOnlyList<PolicyLayer>? presentLayers = null,
            int queuedRunCount = 0, int pendingApprovalCount = 0, CancellationToken ct = default)
        {
            AnalyzeCalls++;
            return _failCode is null
                ? Task.FromResult(Result<PolicyImpactReport>.Ok(_report!))
                : Task.FromResult(Result<PolicyImpactReport>.Fail(PolicyErrors.Instance.Error(_failCode)));
        }
    }

    private sealed class FakeEpoch : IRevocationEpoch
    {
        public long Current { get; private set; }
        public int BumpCalls { get; private set; }
        public void Bump() { Current++; BumpCalls++; }
    }

    private sealed class FakeGrantStore : IGrantStore
    {
        public int IssueCalls;
        public Task<Result<PolicyGrantId>> IssueAsync(PolicyGrant grant, IClock clock, CancellationToken ct = default)
        { IssueCalls++; return Task.FromResult(Result<PolicyGrantId>.Ok(grant.GrantId)); }
        public Task<Result<PolicyGrant>> GetAsync(PolicyGrantId id, CancellationToken ct = default)
            => Task.FromResult(Result<PolicyGrant>.Fail(PolicyErrors.GrantNotFoundError(id.Value)));
        public Task<Result<IReadOnlyList<PolicyGrant>>> ListByAutomationAsync(string automationId, string revisionId, CancellationToken ct = default)
            => Task.FromResult(Result<IReadOnlyList<PolicyGrant>>.Ok(Array.Empty<PolicyGrant>()));
        public Task<Result<IReadOnlyList<PolicyGrant>>> ListActiveGrantsAsync(string capabilityStableId, string sourceKind, string sourceStableId, string? schemaSha256, DateTimeOffset nowUtc, CancellationToken ct = default)
            => Task.FromResult(Result<IReadOnlyList<PolicyGrant>>.Ok(Array.Empty<PolicyGrant>()));
        public Task<Result<IReadOnlyList<PolicyGrant>>> ListActiveAsync(DateTimeOffset nowUtc, CancellationToken ct = default)
            => Task.FromResult(Result<IReadOnlyList<PolicyGrant>>.Ok(Array.Empty<PolicyGrant>()));
        public Task<Result<PolicyGrant>> RevokeAsync(PolicyGrantId id, IClock clock, CancellationToken ct = default)
            => Task.FromResult(Result<PolicyGrant>.Fail(PolicyErrors.GrantNotFoundError(id.Value)));
    }
}
