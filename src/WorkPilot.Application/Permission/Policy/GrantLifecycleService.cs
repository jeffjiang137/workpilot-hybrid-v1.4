using System;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.PermissionGovernance;
using WorkPilot.Domain.PermissionGovernance.Evaluation;

namespace WorkPilot.Application.Permission.Policy;

/// <summary>
/// Application service orchestrating the AutomationGrant lifecycle (PER-004). It enforces the two
/// structural gates the domain model cannot (because they need external context): the grant scope
/// must be contained within the capability's declared manifest scope, and the grant must target the
/// capability's current schema. The store persists the immutable grant and the revocation is
/// immediate (PER-007). A grant is bound to a concrete <c>(automation_id, revision_id)</c> pair, so
/// saving a new revision never inherits old grants (doc 07 §8 / §17).
/// </summary>
public sealed class GrantLifecycleService
{
    private readonly IGrantStore _grants;
    private readonly IIdGenerator _ids;

    public GrantLifecycleService(IGrantStore grants, IIdGenerator ids)
    {
        _grants = grants ?? throw new ArgumentNullException(nameof(grants));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
    }

    /// <summary>
    /// Issues a Medium grant. Fails closed if the scope would exceed the manifest scope, or if the
    /// schema does not match the capability's current schema. Risk ceiling (Medium) and duration
    /// (≤30d) are enforced by <see cref="PolicyGrant.Validate"/> inside <see cref="PolicyGrant.Create"/>.
    /// </summary>
    public async Task<Result<PolicyGrant>> IssueGrantAsync(
        GrantIssueRequest req,
        ResourceScope manifestScope,
        string capabilitySchemaSha,
        long currentRevocationEpoch,
        IClock clock,
        CancellationToken ct = default)
    {
        var inter = ScopeIntersector.Intersect(req.ResourceScope, manifestScope);
        if (inter.Outcome == ScopeIntersector.Kind.Disjoint)
            return Result<PolicyGrant>.Fail(PolicyErrors.GrantScopeExceedsManifestError(req.CapabilityStableId));
        // A grant is within the manifest iff the intersection equals the grant's effective scope.
        // Compare canonical JSON (not record equality): ResourceScope lists use reference equality,
        // so record == would wrongly reject a valid subset (PER-004 scope-containment).
        if (inter.Outcome == ScopeIntersector.Kind.Bounded
            && inter.Scope is not null
            && inter.Scope.ToStorageJson() != req.ResourceScope.ToStorageJson())
            return Result<PolicyGrant>.Fail(PolicyErrors.GrantScopeExceedsManifestError(req.CapabilityStableId));

        if (!string.Equals(req.SchemaSha256, capabilitySchemaSha, StringComparison.Ordinal))
            return Result<PolicyGrant>.Fail(PolicyErrors.GrantSchemaMismatchError(req.SchemaSha256, capabilitySchemaSha));

        var grant = PolicyGrant.Create(_ids, req, clock.UtcNow, currentRevocationEpoch);
        var issued = await _grants.IssueAsync(grant, clock, ct);
        return !issued.IsSuccess ? Result<PolicyGrant>.Fail(issued.Error!) : Result<PolicyGrant>.Ok(grant);
    }

    public Task<Result<PolicyGrant>> GetGrantAsync(PolicyGrantId id, CancellationToken ct = default)
        => _grants.GetAsync(id, ct);

    public Task<Result<IReadOnlyList<PolicyGrant>>> ListByAutomationAsync(
        string automationId, string revisionId, CancellationToken ct = default)
        => _grants.ListByAutomationAsync(automationId, revisionId, ct);

    /// <summary>Revokes a grant. Idempotent and fail-closed: unknown id fails; already-revoked returns the existing record.</summary>
    public Task<Result<PolicyGrant>> RevokeGrantAsync(PolicyGrantId id, IClock clock, CancellationToken ct = default)
        => _grants.RevokeAsync(id, clock, ct);

    /// <summary>Resolves whether an active (not revoked, not expired) grant exists for the capability/source/schema now. Gate hook (T17/§11).</summary>
    public async Task<Result<bool>> HasActiveGrantAsync(
        string capabilityStableId, string sourceKind, string sourceId, string schemaSha256,
        DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        var active = await _grants.ListActiveGrantsAsync(capabilityStableId, sourceKind, sourceId, schemaSha256, nowUtc, ct);
        if (!active.IsSuccess)
            return Result<bool>.Fail(active.Error!);
        return Result<bool>.Ok(active.Value!.Count > 0);
    }
}
