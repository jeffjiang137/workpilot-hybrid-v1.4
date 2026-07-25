using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.PermissionGovernance;

namespace WorkPilot.Application.Permission.Policy;

/// <summary>The current, effective policy: document pointer + immutable version + its statements.</summary>
public sealed record CurrentPolicyBundle(
    PolicyDocument Document,
    PolicyVersion Version,
    IReadOnlyList<PolicyStatement> Statements);

/// <summary>A page of policy audit records (SEC-106 retrieval).</summary>
public sealed record PolicyAuditPage(IReadOnlyList<PolicyAuditRecord> Items, int Total);

/// <summary>
/// Persistence + lifecycle port for policy governance (PER-001/009/010, SEC-106). Implemented by
/// <c>SqlitePolicyStore</c>. Key invariants:
/// <list type="bullet">
///   <item><description>Versions are immutable — <see cref="SaveNewVersionAsync"/> and
///     <see cref="RecoverDefaultAsync"/> only INSERT new versions and CAS the current pointer; old
///     versions are never mutated or deleted (PER-009).</description></item>
///   <item><description>Recovery only generates a new version and must NOT delete historical audit
///     (PER-010); consent receipts bound to a superseded policy hash are invalidated (旧 receipt invalid).</description></item>
///   <item><description>Audit records are append-only and retrievable/verifiable (SEC-106).</description></item>
/// </list>
/// </summary>
public interface IPolicyStore
{
    /// <summary>
    /// Saves a new immutable version of an existing document. The store assigns the next version
    /// number, re-validates and rebinds the statements to the new version id, CAS-updates
    /// <c>current_version_id</c>, and writes a user_save audit entry.
    /// </summary>
    Task<Result<PolicyVersionId>> SaveNewVersionAsync(
        PolicyDocumentId documentId,
        IReadOnlyList<PolicyStatement> statements,
        string actor,
        string reasonCode,
        IClock clock,
        CancellationToken ct = default);

    /// <summary>
    /// Recovers the minimum-permission default policy for a layer/scope (PER-010). If no document
    /// exists it is created; otherwise a NEW version is appended. Never deletes audit; invalidates
    /// any consent receipts bound to the previously-current policy hash.
    /// </summary>
    Task<Result<PolicyVersionId>> RecoverDefaultAsync(
        PolicyLayer layer,
        string? scopeId,
        IClock clock,
        CancellationToken ct = default);

    /// <summary>Returns the current effective policy bundle for a layer/scope, or a failure if none.</summary>
    Task<Result<CurrentPolicyBundle>> GetCurrentAsync(
        PolicyLayer layer,
        string? scopeId,
        CancellationToken ct = default);

    /// <summary>Retrieves a page of policy audit records ordered by time descending (SEC-106).</summary>
    Task<Result<PolicyAuditPage>> ListAuditAsync(int limit, int offset, CancellationToken ct = default);

    /// <summary>
    /// Verifies that every stored version's <c>canonical_sha256</c> matches the recomputed hash of its
    /// statements (SEC-106 integrity). Returns false on any mismatch (a Critical event must be raised
    /// by the caller).
    /// </summary>
    Task<Result<bool>> VerifyIntegrityAsync(CancellationToken ct = default);

    /// <summary>Marks all consent receipts bound to <paramref name="policyHash"/> as invalidated (旧 receipt invalid).</summary>
    Task InvalidateReceiptsForPolicyHashAsync(string policyHash, IClock clock, CancellationToken ct = default);

    /// <summary>
    /// Bootstrap helper: ensures the BuiltInSafety and GlobalPolicy baseline documents exist
    /// (seeded as minimum-permission defaults). Does not expand V1.4 permissions. Safe to call
    /// repeatedly — only creates missing baselines.
    /// </summary>
    Task EnsureDefaultPolicyAsync(IClock clock, CancellationToken ct = default);
}
