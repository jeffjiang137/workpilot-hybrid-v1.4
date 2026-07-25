using System;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;

namespace WorkPilot.Domain.PermissionGovernance;

/// <summary>
/// An immutable snapshot of a policy document's statements (PER-009). Saving a new version never
/// mutates an existing one; the canonical SHA-256 of the version's statements is recorded and later
/// verified for integrity (SEC-106). <see cref="IsDefault"/> marks the bootstrapped minimum-permission
/// baseline produced by <see cref="DefaultPolicyProvider"/>.
/// </summary>
public sealed record PolicyVersion(
    PolicyVersionId Id,
    PolicyDocumentId DocumentId,
    int VersionNumber,
    string CanonicalSha256,
    string DocumentJson,
    bool IsDefault,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// A policy document: one per layer (+ optional scope). Holds the pointer to the current
/// <see cref="PolicyVersion"/>. Edits create new versions and CAS this pointer (never in-place edits).
/// </summary>
public sealed record PolicyDocument(
    PolicyDocumentId Id,
    PolicyLayer Layer,
    string? ScopeId,
    PolicyVersionId? CurrentVersionId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// An append-only policy audit record (SEC-106). Records every policy lifecycle action with an
/// integrity-relevant <see cref="PolicyHash"/> (the current version's canonical hash) so audits can
/// be retrieved, displayed, and verified. <see cref="Source"/> distinguishes bootstrap/recovery/
/// user-save/grant/receipt/legacy_v14 entries; legacy V1.4 entries are tagged so they are never
/// confused with V1.5 policy audit (T16: "legacy audit 标识").
/// </summary>
public sealed record PolicyAuditRecord(
    PolicyAuditId Id,
    DateTimeOffset OccurredAtUtc,
    PolicyLayer? Layer,
    PolicyAuditAction Action,
    PolicyDocumentId? DocumentId,
    PolicyVersionId? VersionId,
    string? ReasonCode,
    string? Actor,
    PolicyAuditSource Source,
    string DetailJson,
    string? PolicyHash,
    DateTimeOffset CreatedAtUtc)
{
    public static PolicyAuditRecord Create(
        IIdGenerator ids,
        DateTimeOffset occurredAt,
        PolicyLayer? layer,
        PolicyAuditAction action,
        PolicyDocumentId? documentId,
        PolicyVersionId? versionId,
        string? reasonCode,
        string? actor,
        PolicyAuditSource source,
        string detailJson,
        string? policyHash)
        => new(
            PolicyAuditId.Create(ids), occurredAt, layer, action, documentId, versionId,
            reasonCode, actor, source, detailJson, policyHash, occurredAt);
}

/// <summary>Policy audit action (stored as stable string in <c>policy_audit.action</c>).</summary>
public enum PolicyAuditAction : int
{
    Bootstrap = 0,
    Recovery = 1,
    UserSave = 2,
    GrantIssued = 3,
    GrantRevoked = 4,
    ReceiptConsumed = 5,
    ReceiptInvalidated = 6,
    LegacyV14 = 7,
    IntegrityCheck = 8
}

/// <summary>Provenance of a policy audit record (stored as stable string in <c>policy_audit.source</c>).</summary>
public enum PolicyAuditSource : int
{
    Bootstrap = 0,
    Recovery = 1,
    UserSave = 2,
    Grant = 3,
    Receipt = 4,
    LegacyV14 = 5
}
