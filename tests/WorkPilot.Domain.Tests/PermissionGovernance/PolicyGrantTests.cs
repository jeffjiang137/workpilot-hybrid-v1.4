using System;
using System.Collections.Generic;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.PermissionGovernance;
using Xunit;

namespace WorkPilot.Domain.Tests.PermissionGovernance;

public class PolicyGrantTests
{
    private static readonly SequentialIdGenerator Ids = new();
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly ResourceScope Scope = new LocalProjectScope("proj-1", new List<string> { "src" }, new List<string> { "read" });

    private static GrantIssueRequest ValidRequest(RiskLevel ceiling = RiskLevel.Medium, TimeSpan? duration = null, string RevisionId = "rev-1")
        => new(
            AutomationId: "auto-1",
            RevisionId: RevisionId,
            SpaceId: "space-1",
            ExpertRevisionId: "exp-1",
            SourceKind: "mcp",
            SourceId: "src-1",
            CapabilityStableId: "cap-1",
            SchemaSha256: "sha-cap",
            ResourceScope: Scope,
            RiskCeiling: ceiling,
            NotBeforeUtc: Now,
            ExpiresAtUtc: Now + (duration ?? TimeSpan.FromDays(7)));

    [Fact]
    public void Create_produces_valid_medium_grant()
    {
        var grant = PolicyGrant.Create(Ids, ValidRequest(), Now, revocationEpoch: 1);
        Assert.Equal(RiskLevel.Medium, grant.RiskCeiling);
        Assert.Equal("auto-1", grant.AutomationId);
        Assert.Equal("rev-1", grant.RevisionId);
        Assert.Equal(1, grant.RevocationEpochAtIssue);
        Assert.False(string.IsNullOrEmpty(grant.ScopeSha256));
        Assert.True(grant.IsActive(Now));
    }

    [Fact]
    public void Validate_rejects_non_medium_risk_ceiling()
    {
        // Create() validates, so a Low ceiling must throw at construction time.
        Assert.Throws<InvalidOperationException>(() =>
            PolicyGrant.Create(Ids, ValidRequest(RiskLevel.Low), Now, 1));

        // And a bypass-constructed grant must report the structural failure with the right code.
        var built = BuildBypassingCreate(RiskLevel.Low);
        var r = built.Validate();
        Assert.False(r.IsSuccess);
        Assert.Equal("POLICY_GRANT_RISK_CEILING", r.Error!.Code);
    }

    [Fact]
    public void Create_throws_on_high_risk_ceiling()
    {
        Assert.Throws<InvalidOperationException>(() =>
            PolicyGrant.Create(Ids, ValidRequest(RiskLevel.High), Now, 1));
        Assert.Throws<InvalidOperationException>(() =>
            PolicyGrant.Create(Ids, ValidRequest(RiskLevel.Critical), Now, 1));
    }

    [Fact]
    public void Validate_rejects_duration_over_30_days()
    {
        var g = BuildBypassingCreate(RiskLevel.Medium, duration: TimeSpan.FromDays(31));
        var r = g.Validate();
        Assert.False(r.IsSuccess);
        Assert.Equal("POLICY_GRANT_DURATION", r.Error!.Code);
    }

    [Fact]
    public void Validate_rejects_not_before_after_expires()
    {
        var g = BuildBypassingCreate(RiskLevel.Medium, notBefore: Now + TimeSpan.FromDays(2), expires: Now + TimeSpan.FromDays(1));
        var r = g.Validate();
        Assert.False(r.IsSuccess);
        Assert.Equal("POLICY_GRANT_TIME_RANGE", r.Error!.Code);
    }

    [Fact]
    public void Validate_rejects_revoked_before_created()
    {
        var g = BuildBypassingCreate(RiskLevel.Medium, created: Now - TimeSpan.FromDays(1), revoked: Now - TimeSpan.FromDays(2));
        var r = g.Validate();
        Assert.False(r.IsSuccess);
        Assert.Equal("POLICY_GRANT_REVOKED_BEFORE_CREATED", r.Error!.Code);
    }

    [Fact]
    public void Status_prefers_revoked_over_expired()
    {
        var revoked = BuildBypassingCreate(RiskLevel.Medium, expires: Now - TimeSpan.FromDays(1), revoked: Now - TimeSpan.FromHours(1));
        Assert.Equal(GrantStatus.Revoked, revoked.Status(Now));

        var expired = BuildBypassingCreate(RiskLevel.Medium, expires: Now - TimeSpan.FromDays(1));
        Assert.Equal(GrantStatus.Expired, expired.Status(Now));

        var active = BuildBypassingCreate(RiskLevel.Medium, expires: Now + TimeSpan.FromDays(1));
        Assert.Equal(GrantStatus.Active, active.Status(Now));
    }

    [Fact]
    public void ComputeScopeSha256_is_deterministic_and_order_independent_of_record()
    {
        var a = PolicyGrant.ComputeScopeSha256(Scope);
        var b = PolicyGrant.ComputeScopeSha256(new LocalProjectScope("proj-1", new List<string> { "src" }, new List<string> { "read" }));
        Assert.Equal(a, b);
        Assert.NotEqual(a, PolicyGrant.ComputeScopeSha256(new LocalProjectScope("proj-2", new List<string> { "src" }, new List<string> { "read" })));
    }

    [Fact]
    public void Revoke_is_immutable_and_idempotent()
    {
        var g = PolicyGrant.Create(Ids, ValidRequest(), Now, 1);
        var revoked = g.Revoke(Now + TimeSpan.FromMinutes(5));
        Assert.Null(g.RevokedAtUtc);
        Assert.NotNull(revoked.RevokedAtUtc);
        Assert.Equal(GrantStatus.Revoked, revoked.Status(Now + TimeSpan.FromMinutes(5)));

        // Revoking again keeps the earlier timestamp (no double mutation).
        var second = revoked.Revoke(Now + TimeSpan.FromMinutes(10));
        Assert.Equal(revoked.RevokedAtUtc, second.RevokedAtUtc);
    }

    [Fact]
    public void Grant_bound_to_revision_does_not_inherit_to_new_revision()
    {
        // Model-level invariant: a grant references a concrete (automation, revision) pair. Saving a
        // new revision yields a new revision id, so the old grant is simply not matched for the new
        // revision — there is no shared mutable state to "inherit".
        var forRev1 = PolicyGrant.Create(Ids, ValidRequest(RevisionId: "rev-1"), Now, 1);
        var forRev2 = PolicyGrant.Create(Ids, ValidRequest(RevisionId: "rev-2"), Now, 1);
        Assert.NotEqual(forRev1.GrantId, forRev2.GrantId);
        Assert.Equal("rev-1", forRev1.RevisionId);
        Assert.Equal("rev-2", forRev2.RevisionId);
    }

    // Helper: constructs a PolicyGrant bypassing the Create-time validation (for negative tests).
    private static PolicyGrant BuildBypassingCreate(
        RiskLevel ceiling,
        TimeSpan? duration = null,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? expires = null,
        DateTimeOffset? created = null,
        DateTimeOffset? revoked = null)
    {
        var nb = notBefore ?? Now;
        var ex = expires ?? (nb + (duration ?? TimeSpan.FromDays(7)));
        return new PolicyGrant(
            PolicyGrantId.Create(Ids),
            "auto-1", "rev-1", "space-1", "exp-1",
            "mcp", "src-1", "cap-1", "sha-cap",
            Scope, PolicyGrant.ComputeScopeSha256(Scope),
            ceiling, nb, ex, 1, created ?? Now, revoked);
    }
}
