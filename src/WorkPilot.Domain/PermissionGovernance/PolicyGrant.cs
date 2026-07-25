using System;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;

namespace WorkPilot.Domain.PermissionGovernance;

/// <summary>
/// Lifecycle status of an <see cref="PolicyGrant"/> (doc 07 §8). Derived, never stored — the table
/// holds <c>revoked_at_utc</c> and <c>expires_at_utc</c>; <see cref="PolicyGrant.Status"/> computes
/// the status against the current clock. Revoked wins over Expired.
/// </summary>
public enum GrantStatus : int
{
    Active = 0,
    Expired = 1,
    Revoked = 2
}

/// <summary>
/// Inputs to issue a grant. The caller (Application service) is responsible for binding the grant to
/// a specific <c>revision_id</c> and ensuring the <see cref="ResourceScope"/> does not exceed the
/// capability's manifest scope (PER-004) — that scope-containment check is a service concern because
/// it needs the manifest; this record only carries the resolved scope.
/// </summary>
public sealed record GrantIssueRequest(
    string AutomationId,
    string RevisionId,
    string? SpaceId,
    string? ExpertRevisionId,
    string SourceKind,
    string SourceId,
    string CapabilityStableId,
    string SchemaSha256,
    ResourceScope ResourceScope,
    RiskLevel RiskCeiling,
    DateTimeOffset NotBeforeUtc,
    DateTimeOffset ExpiresAtUtc);

/// <summary>
/// An explicit, time-boxed Medium-risk authorization for an automation to exercise a capability
/// (PER-004, doc 07 §8). Grants are immutable once issued; revocation sets <see cref="RevokedAtUtc"/>
/// but never deletes the row (audit). A grant is bound to a concrete <c>(automation_id, revision_id)</c>
/// pair, so saving a new automation revision never inherits old grants (doc 07 §8 / §17: "新增来源/
/// 能力/Schema 永不继承旧 Allow"). Grants carry no secret.
/// </summary>
public sealed record PolicyGrant(
    PolicyGrantId GrantId,
    string AutomationId,
    string RevisionId,
    string? SpaceId,
    string? ExpertRevisionId,
    string SourceKind,
    string SourceId,
    string CapabilityStableId,
    string SchemaSha256,
    ResourceScope ResourceScope,
    string ScopeSha256,
    RiskLevel RiskCeiling,
    DateTimeOffset NotBeforeUtc,
    DateTimeOffset ExpiresAtUtc,
    long RevocationEpochAtIssue,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? RevokedAtUtc)
{
    /// <summary>Deterministic SHA-256 of the canonicalized resource scope (stored as <c>scope_sha256</c>).</summary>
    public static string ComputeScopeSha256(ResourceScope scope)
        => JcsCanonicalizer.CanonicalizeToSha256(scope.ToStorageJson());

    /// <summary>Derived status. Revoked takes precedence over Expired (PER-007: revocation immediate).</summary>
    public GrantStatus Status(DateTimeOffset nowUtc)
    {
        if (RevokedAtUtc is not null)
            return GrantStatus.Revoked;
        if (nowUtc >= ExpiresAtUtc)
            return GrantStatus.Expired;
        return GrantStatus.Active;
    }

    public bool IsActive(DateTimeOffset nowUtc) => Status(nowUtc) == GrantStatus.Active;

    /// <summary>
    /// Structural invariants only (PER-004): risk ceiling fixed at Medium; duration ≤
    /// <see cref="Limits.V1_5.MaxGrantDurationDays"/>; not_before ≤ expires; revoke cannot precede
    /// creation. Scope-containment and schema-binding checks are performed by the issuing service.
    /// </summary>
    public Result Validate()
    {
        if (RiskCeiling != RiskLevel.Medium)
            return Result.Failure(PolicyErrors.GrantRiskCeilingError());
        var maxDuration = TimeSpan.FromDays(Limits.V1_5.MaxGrantDurationDays);
        if (ExpiresAtUtc - NotBeforeUtc > maxDuration)
            return Result.Failure(PolicyErrors.GrantDurationError());
        if (NotBeforeUtc > ExpiresAtUtc)
            return Result.Failure(PolicyErrors.GrantTimeRangeError());
        if (RevokedAtUtc is not null && RevokedAtUtc.Value < CreatedAtUtc)
            return Result.Failure(PolicyErrors.GrantRevokedBeforeCreatedError());
        return Result.Success();
    }

    /// <summary>Returns a copy with <see cref="RevokedAtUtc"/> set (immutability-preserving). Idempotent-safe: revoking an already-revoked grant keeps the earlier timestamp.</summary>
    public PolicyGrant Revoke(DateTimeOffset nowUtc)
        => RevokedAtUtc is not null ? this : this with { RevokedAtUtc = nowUtc };

    /// <summary>Factory that computes <see cref="ScopeSha256"/> and validates before construction.</summary>
    public static PolicyGrant Create(
        IIdGenerator ids,
        GrantIssueRequest req,
        DateTimeOffset nowUtc,
        long revocationEpoch)
    {
        var grant = new PolicyGrant(
            PolicyGrantId.Create(ids),
            req.AutomationId, req.RevisionId, req.SpaceId, req.ExpertRevisionId,
            req.SourceKind, req.SourceId, req.CapabilityStableId, req.SchemaSha256,
            req.ResourceScope, ComputeScopeSha256(req.ResourceScope), req.RiskCeiling,
            req.NotBeforeUtc, req.ExpiresAtUtc, revocationEpoch, nowUtc, null);
        var validation = grant.Validate();
        if (!validation.IsSuccess)
            throw new InvalidOperationException(validation.Error!.MessageKey);
        return grant;
    }
}
