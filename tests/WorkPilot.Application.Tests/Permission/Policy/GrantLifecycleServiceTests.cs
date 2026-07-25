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

public class GrantLifecycleServiceTests
{
    private static readonly SequentialIdGenerator Ids = new();
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly IClock Clock = new FakeClock(Now);

    private static readonly ResourceScope Manifest = new LocalProjectScope("proj-1", new List<string> { "src" }, new List<string> { "read", "write" });
    private static readonly ResourceScope WithinManifest = new LocalProjectScope("proj-1", new List<string> { "src" }, new List<string> { "read" });
    private static readonly ResourceScope OutsideManifest = new LocalProjectScope("proj-1", new List<string> { "src", "secrets" }, new List<string> { "read" });
    private static readonly ResourceScope OtherProject = new LocalProjectScope("proj-2", new List<string> { "src" }, new List<string> { "read" });

    private static GrantIssueRequest Req(ResourceScope scope, string schema = "sha-cap")
        => new(
            AutomationId: "auto-1", RevisionId: "rev-1", SpaceId: "space-1", ExpertRevisionId: "exp-1",
            SourceKind: "mcp", SourceId: "src-1", CapabilityStableId: "cap-1", SchemaSha256: schema,
            ResourceScope: scope, RiskCeiling: RiskLevel.Medium,
            NotBeforeUtc: Now, ExpiresAtUtc: Now + TimeSpan.FromDays(7));

    [Fact]
    public async Task Issue_succeeds_when_scope_within_manifest_and_schema_matches()
    {
        var store = new FakeGrantStore();
        var svc = new GrantLifecycleService(store, Ids);

        var r = await svc.IssueGrantAsync(Req(WithinManifest), Manifest, "sha-cap", currentRevocationEpoch: 1, Clock, CancellationToken.None);
        Assert.True(r.IsSuccess, r.Error?.Code);
        Assert.Equal("auto-1", r.Value!.AutomationId);
        Assert.Single(store.Issued);
    }

    [Fact]
    public async Task Issue_fails_when_scope_exceeds_manifest()
    {
        var svc = new GrantLifecycleService(new FakeGrantStore(), Ids);
        var r = await svc.IssueGrantAsync(Req(OutsideManifest), Manifest, "sha-cap", 1, Clock, CancellationToken.None);
        Assert.False(r.IsSuccess);
        Assert.Equal("POLICY_GRANT_SCOPE_EXCEEDS_MANIFEST", r.Error!.Code);
    }

    [Fact]
    public async Task Issue_fails_when_scope_disjoint_from_manifest()
    {
        var svc = new GrantLifecycleService(new FakeGrantStore(), Ids);
        var r = await svc.IssueGrantAsync(Req(OtherProject), Manifest, "sha-cap", 1, Clock, CancellationToken.None);
        Assert.False(r.IsSuccess);
        Assert.Equal("POLICY_GRANT_SCOPE_EXCEEDS_MANIFEST", r.Error!.Code);
    }

    [Fact]
    public async Task Issue_fails_when_schema_mismatches_capability()
    {
        var svc = new GrantLifecycleService(new FakeGrantStore(), Ids);
        var r = await svc.IssueGrantAsync(Req(WithinManifest, schema: "sha-grant"), Manifest, "sha-capability", 1, Clock, CancellationToken.None);
        Assert.False(r.IsSuccess);
        Assert.Equal("POLICY_GRANT_SCHEMA_MISMATCH", r.Error!.Code);
    }

    [Fact]
    public async Task Revoke_is_delegated_and_idempotent()
    {
        var store = new FakeGrantStore();
        var svc = new GrantLifecycleService(store, Ids);
        var issued = (await svc.IssueGrantAsync(Req(WithinManifest), Manifest, "sha-cap", 1, Clock, CancellationToken.None)).Value!;

        var revoked = await svc.RevokeGrantAsync(issued.GrantId, Clock, CancellationToken.None);
        Assert.True(revoked.IsSuccess);
        Assert.NotNull(revoked.Value!.RevokedAtUtc);

        // Second revoke on an already-revoked grant returns the existing record (idempotent).
        var again = await svc.RevokeGrantAsync(issued.GrantId, Clock, CancellationToken.None);
        Assert.True(again.IsSuccess);
        Assert.Equal(revoked.Value!.RevokedAtUtc, again.Value!.RevokedAtUtc);
    }

    [Fact]
    public async Task HasActiveGrant_reflects_issued_then_revoked()
    {
        var store = new FakeGrantStore();
        var svc = new GrantLifecycleService(store, Ids);
        var issued = (await svc.IssueGrantAsync(Req(WithinManifest), Manifest, "sha-cap", 1, Clock, CancellationToken.None)).Value!;

        var before = await svc.HasActiveGrantAsync("cap-1", "mcp", "src-1", "sha-cap", Now, CancellationToken.None);
        Assert.True(before.Value);

        await svc.RevokeGrantAsync(issued.GrantId, Clock, CancellationToken.None);
        var after = await svc.HasActiveGrantAsync("cap-1", "mcp", "src-1", "sha-cap", Now, CancellationToken.None);
        Assert.False(after.Value);
    }

    [Fact]
    public async Task New_revision_does_not_inherit_old_grant()
    {
        var store = new FakeGrantStore();
        var svc = new GrantLifecycleService(store, Ids);
        await svc.IssueGrantAsync(Req(WithinManifest, schema: "sha-cap"), Manifest, "sha-cap", 1, Clock, CancellationToken.None);

        // A grant issued for rev-2 is a distinct row; listing by rev-1 returns nothing for rev-2.
        var rev1 = await svc.ListByAutomationAsync("auto-1", "rev-1", CancellationToken.None);
        var rev2 = await svc.ListByAutomationAsync("auto-1", "rev-2", CancellationToken.None);
        Assert.Single(rev1.Value!);
        Assert.Empty(rev2.Value!);
    }

    private sealed class FakeGrantStore : IGrantStore
    {
        public List<PolicyGrant> Issued { get; } = new();
        private readonly Dictionary<string, PolicyGrant> _byId = new();

        public Task<Result<PolicyGrantId>> IssueAsync(PolicyGrant grant, IClock clock, CancellationToken ct = default)
        {
            Issued.Add(grant);
            _byId[grant.GrantId.Value] = grant;
            return Task.FromResult(Result<PolicyGrantId>.Ok(grant.GrantId));
        }

        public Task<Result<PolicyGrant>> GetAsync(PolicyGrantId id, CancellationToken ct = default)
            => Task.FromResult(_byId.TryGetValue(id.Value, out var g)
                ? Result<PolicyGrant>.Ok(g)
                : Result<PolicyGrant>.Fail(PolicyErrors.GrantNotFoundError(id.Value)));

        public Task<Result<IReadOnlyList<PolicyGrant>>> ListByAutomationAsync(string automationId, string revisionId, CancellationToken ct = default)
            => Task.FromResult(Result<IReadOnlyList<PolicyGrant>>.Ok(
                Issued.Where(g => g.AutomationId == automationId && g.RevisionId == revisionId).ToList()));

        public Task<Result<IReadOnlyList<PolicyGrant>>> ListActiveGrantsAsync(string capabilityStableId, string sourceKind, string sourceId, string schemaSha256, DateTimeOffset nowUtc, CancellationToken ct = default)
            => Task.FromResult(Result<IReadOnlyList<PolicyGrant>>.Ok(
                Issued.Where(g => g.CapabilityStableId == capabilityStableId && g.SourceKind == sourceKind
                    && g.SourceId == sourceId && g.SchemaSha256 == schemaSha256 && g.IsActive(nowUtc)).ToList()));

        public Task<Result<IReadOnlyList<PolicyGrant>>> ListActiveAsync(DateTimeOffset nowUtc, CancellationToken ct = default)
            => Task.FromResult(Result<IReadOnlyList<PolicyGrant>>.Ok(Issued.Where(g => g.IsActive(nowUtc)).ToList()));

        public Task<Result<PolicyGrant>> RevokeAsync(PolicyGrantId id, IClock clock, CancellationToken ct = default)
        {
            if (!_byId.TryGetValue(id.Value, out var g))
                return Task.FromResult(Result<PolicyGrant>.Fail(PolicyErrors.GrantNotFoundError(id.Value)));
            if (g.RevokedAtUtc is not null)
                return Task.FromResult(Result<PolicyGrant>.Ok(g));
            var revoked = g.Revoke(clock.UtcNow);
            _byId[id.Value] = revoked;
            var idx = Issued.FindIndex(x => x.GrantId.Value == id.Value);
            if (idx >= 0) Issued[idx] = revoked;
            return Task.FromResult(Result<PolicyGrant>.Ok(revoked));
        }
    }
}
