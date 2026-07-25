using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.PermissionGovernance;

namespace WorkPilot.Application.Permission.Policy;

/// <summary>
/// Persistence port for AutomationGrant lifecycle (PER-004). Implemented by
/// <c>SqlitePolicyStore</c> against the <c>policy_grants</c> table (Migration 019). Grants are
/// INSERT-only on issue; revocation sets <c>revoked_at_utc</c> but never deletes the row (audit).
/// A grant is bound to a concrete <c>(automation_id, revision_id)</c> pair, so saving a new
/// automation revision never inherits old grants (doc 07 §8 / §17).
/// </summary>
public interface IGrantStore
{
    /// <summary>Issues a grant: INSERTs the row and writes a <c>grant_issued</c> audit entry.</summary>
    Task<Result<PolicyGrantId>> IssueAsync(PolicyGrant grant, IClock clock, CancellationToken ct = default);

    /// <summary>Returns the grant (with its revocation state) or a failure if not found.</summary>
    Task<Result<PolicyGrant>> GetAsync(PolicyGrantId id, CancellationToken ct = default);

    /// <summary>All grants (active or revoked) bound to a specific automation revision.</summary>
    Task<Result<IReadOnlyList<PolicyGrant>>> ListByAutomationAsync(string automationId, string revisionId, CancellationToken ct = default);

    /// <summary>
    /// Grants that are currently usable for a capability/source/schema at <paramref name="nowUtc"/>
    /// (not revoked and not expired). Used by the gate to resolve <c>AutomationGrantPresent</c>.
    /// </summary>
    Task<Result<IReadOnlyList<PolicyGrant>>> ListActiveGrantsAsync(
        string capabilityStableId, string sourceKind, string sourceId, string schemaSha256,
        DateTimeOffset nowUtc, CancellationToken ct = default);

    /// <summary>
    /// Every grant that is still usable at <paramref name="nowUtc"/> (not revoked, not expired),
    /// across all capabilities/sources — used by the Security Center grants tab (SEC-101).
    /// </summary>
    Task<Result<IReadOnlyList<PolicyGrant>>> ListActiveAsync(DateTimeOffset nowUtc, CancellationToken ct = default);

    /// <summary>
    /// Revokes a grant (idempotent): sets <c>revoked_at_utc</c> and writes a <c>grant_revoked</c>
    /// audit entry. Revoking an already-revoked grant returns the existing record unchanged.
    /// </summary>
    Task<Result<PolicyGrant>> RevokeAsync(PolicyGrantId id, IClock clock, CancellationToken ct = default);
}
