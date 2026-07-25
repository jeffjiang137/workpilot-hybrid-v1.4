using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.PermissionGovernance;
using WorkPilot.Infrastructure.Data;
using WorkPilot.Infrastructure.Permission.Policy;
using Xunit;

namespace WorkPilot.Infrastructure.Tests.Permission.Policy;

/// <summary>T18: grant persistence — issue/get/list/active/revoke against the policy_grants table (Migration 019).</summary>
public sealed class SqliteGrantStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly IClock Clock = new FakeClock(Now);

    private static async Task<(SqliteConnection Conn, SqlitePolicyStore Store)> NewStoreAsync()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        var migrator = new V15DatabaseMigrator(Clock);
        await migrator.CreatePolicyTablesAsync(conn, TestContext.Current);
        var store = new SqlitePolicyStore(conn, new SequentialIdGenerator());
        return (conn, store);
    }

    private static GrantIssueRequest Req(string automationId = "auto-1", string revisionId = "rev-1", DateTimeOffset? notBefore = null, DateTimeOffset? expires = null, RiskLevel ceiling = RiskLevel.Medium)
        => new(
            AutomationId: automationId,
            RevisionId: revisionId,
            SpaceId: "space-1",
            ExpertRevisionId: "exp-1",
            SourceKind: "mcp",
            SourceId: "src-1",
            CapabilityStableId: "cap-1",
            SchemaSha256: "sha-cap",
            ResourceScope: new LocalProjectScope("proj-1", new List<string> { "src" }, new List<string> { "read" }),
            RiskCeiling: ceiling,
            NotBeforeUtc: notBefore ?? Now,
            ExpiresAtUtc: expires ?? Now + TimeSpan.FromDays(7));

    [Fact]
    public async Task Issue_then_Get_roundtrips_grant()
    {
        var (conn, store) = await NewStoreAsync();
        try
        {
            var ids = new SequentialIdGenerator();
            var grant = PolicyGrant.Create(ids, Req(), Now, revocationEpoch: 3);
            var issue = await store.IssueAsync(grant, Clock, TestContext.Current);
            Assert.True(issue.IsSuccess);

            var get = await store.GetAsync(grant.GrantId, TestContext.Current);
            Assert.True(get.IsSuccess);
            Assert.Equal(grant.GrantId.Value, get.Value!.GrantId.Value);
            Assert.Equal("auto-1", get.Value.AutomationId);
            Assert.Equal("rev-1", get.Value.RevisionId);
            Assert.Equal(RiskLevel.Medium, get.Value.RiskCeiling);
            Assert.Equal(3, get.Value.RevocationEpochAtIssue);
            Assert.Equal(grant.ScopeSha256, get.Value.ScopeSha256);
            Assert.True(get.Value.IsActive(Now));
        }
        finally { conn.Close(); }
    }

    [Fact]
    public async Task ListByAutomation_scopes_to_revision()
    {
        var (conn, store) = await NewStoreAsync();
        try
        {
            var ids = new SequentialIdGenerator();
            await store.IssueAsync(PolicyGrant.Create(ids, Req("auto-1", "rev-1"), Now, 1), Clock, TestContext.Current);
            await store.IssueAsync(PolicyGrant.Create(ids, Req("auto-1", "rev-2"), Now, 1), Clock, TestContext.Current);

            var r1 = await store.ListByAutomationAsync("auto-1", "rev-1", TestContext.Current);
            var r2 = await store.ListByAutomationAsync("auto-1", "rev-2", TestContext.Current);
            Assert.Single(r1.Value!);
            Assert.Single(r2.Value!);
            Assert.Equal("rev-1", r1.Value![0].RevisionId);
        }
        finally { conn.Close(); }
    }

    [Fact]
    public async Task ListActiveGrants_excludes_expired_and_revoked()
    {
        var (conn, store) = await NewStoreAsync();
        try
        {
            var ids = new SequentialIdGenerator();
            var active = PolicyGrant.Create(ids, Req(expires: Now + TimeSpan.FromDays(7)), Now, 1);
            var expired = PolicyGrant.Create(ids, Req(notBefore: Now - TimeSpan.FromDays(10), expires: Now - TimeSpan.FromDays(1)), Now, 1);
            await store.IssueAsync(active, Clock, TestContext.Current);
            await store.IssueAsync(expired, Clock, TestContext.Current);
            var revoked = PolicyGrant.Create(ids, Req(), Now, 1);
            await store.IssueAsync(revoked, Clock, TestContext.Current);
            await store.RevokeAsync(revoked.GrantId, Clock, TestContext.Current);

            var list = await store.ListActiveGrantsAsync("cap-1", "mcp", "src-1", "sha-cap", Now, TestContext.Current);
            Assert.Single(list.Value!);
            Assert.Equal(active.GrantId.Value, list.Value![0].GrantId.Value);
        }
        finally { conn.Close(); }
    }

    [Fact]
    public async Task Revoke_is_persisted_and_idempotent()
    {
        var (conn, store) = await NewStoreAsync();
        try
        {
            var ids = new SequentialIdGenerator();
            var grant = PolicyGrant.Create(ids, Req(), Now, 1);
            await store.IssueAsync(grant, Clock, TestContext.Current);

            var r1 = await store.RevokeAsync(grant.GrantId, Clock, TestContext.Current);
            Assert.NotNull(r1.Value!.RevokedAtUtc);

            var r2 = await store.RevokeAsync(grant.GrantId, Clock, TestContext.Current);
            Assert.Equal(r1.Value!.RevokedAtUtc, r2.Value!.RevokedAtUtc);

            var get = await store.GetAsync(grant.GrantId, TestContext.Current);
            Assert.NotNull(get.Value!.RevokedAtUtc);
            Assert.Equal(GrantStatus.Revoked, get.Value.Status(Now));
        }
        finally { conn.Close(); }
    }

    [Fact]
    public async Task Issue_and_revoke_write_audit_entries()
    {
        var (conn, store) = await NewStoreAsync();
        try
        {
            var ids = new SequentialIdGenerator();
            var grant = PolicyGrant.Create(ids, Req(), Now, 1);
            await store.IssueAsync(grant, Clock, TestContext.Current);
            await store.RevokeAsync(grant.GrantId, Clock, TestContext.Current);

            var audit = await store.ListAuditAsync(limit: 100, offset: 0, TestContext.Current);
            Assert.Contains(PolicyAuditAction.GrantIssued, audit.Value!.Items.Select(a => a.Action));
            Assert.Contains(PolicyAuditAction.GrantRevoked, audit.Value!.Items.Select(a => a.Action));
        }
        finally { conn.Close(); }
    }
}
