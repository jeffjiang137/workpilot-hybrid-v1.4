using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.PermissionGovernance;
using WorkPilot.Domain.PermissionGovernance.Evaluation;

namespace WorkPilot.Application.Permission.Policy;

/// <summary>
/// Result of a policy save: the new immutable version id plus the pre-save impact report that
/// governed the decision (PER-009/010, doc 07 §13/§15).
/// </summary>
public sealed record PolicySaveResult(PolicyVersionId NewVersionId, PolicyImpactReport Impact);

/// <summary>
/// Orchestrates a user policy save (PER-009/010, doc 07 §13/§15): validates the edited statements,
/// runs pre-save <see cref="IPolicyImpactAnalyzer"/> impact analysis, blocks the save when the target
/// set is too large to analyze completely, enforces the <b>second-confirmation gate</b> for privilege
/// expansion, persists an immutable new version (the store invalidates stale consent receipts on a
/// hash change), and bumps the revocation epoch when the change widens or restricts access so that
/// any previously-issued permit/receipt/grant fails its Current-State Check (doc 07 §11/§17).
/// Saving never issues or extends an <see cref="PolicyGrant"/> — grants are bound to an automation
/// revision and are never inherited by a new revision (PER-010, "新 Revision 不继承旧 Grant").
/// </summary>
public sealed class PolicyAdminService
{
    private readonly IPolicyStore _store;
    private readonly IPolicyImpactAnalyzer _impact;
    private readonly IRevocationEpoch? _epoch;

    public PolicyAdminService(
        IPolicyStore store,
        IPolicyImpactAnalyzer impact,
        IRevocationEpoch? epoch = null)
    {
        _store = store;
        _impact = impact;
        _epoch = epoch;
    }

    public async Task<Result<PolicySaveResult>> SavePolicyAsync(
        PolicyLayer editedLayer,
        string? scopeId,
        IReadOnlyList<PolicyStatement> newStatements,
        IReadOnlyList<ImpactTarget> targets,
        string actor,
        bool confirmedExpansion,
        IClock clock,
        EvaluationContext? baselineContext = null,
        CancellationToken ct = default)
    {
        // 1. Validate the edited statements before any side effect.
        foreach (var s in newStatements)
        {
            var v = s.Validate();
            if (!v.IsSuccess)
                return Result<PolicySaveResult>.Fail(v.Error!);
        }

        // 2. Resolve the document being edited (its layer is the edited layer + scope).
        var docRes = await _store.GetCurrentAsync(editedLayer, scopeId, ct);
        if (!docRes.IsSuccess)
            return Result<PolicySaveResult>.Fail(docRes.Error!);
        var documentId = docRes.Value!.Document.Id;

        // 3. Build old/new snapshots for impact analysis (baseline layers + edited layer substituted).
        var ctx = baselineContext ?? NeutralContext();
        var built = await PolicySnapshotBuilder.BuildAsync(_store, ctx, ct);
        var oldSnapshot = built.Snapshot;
        var newSnapshot = BuildNewSnapshot(oldSnapshot, editedLayer, newStatements);

        // 4. Pre-save impact analysis. A failure (e.g. incomplete target set) blocks the save.
        var impactRes = await _impact.AnalyzeAsync(
            oldSnapshot, newSnapshot, targets, clock.UtcNow, built.PresentLayers,
            queuedRunCount: 0, pendingApprovalCount: 0, ct);
        if (!impactRes.IsSuccess)
            return Result<PolicySaveResult>.Fail(impactRes.Error!);
        var report = impactRes.Value!;

        // 5. Second-confirmation gate: widening access requires explicit confirmation (doc 07 §15).
        if (report.HasPrivilegeExpansion && !confirmedExpansion)
            return Result<PolicySaveResult>.Fail(
                PolicyErrors.ExpansionRequiresConfirmationError(report.AffectedGrantCount));

        // 6. Persist the immutable new version. The store CAS-updates the current pointer and
        //    invalidates consent receipts bound to the superseded policy hash (旧 receipt invalid).
        var reasonCode = report.RequiresEpochBump ? "policy_save_widened" : "policy_save";
        var saveRes = await _store.SaveNewVersionAsync(documentId, newStatements, actor, reasonCode, clock, ct);
        if (!saveRes.IsSuccess)
            return Result<PolicySaveResult>.Fail(saveRes.Error!);

        // 7. Invalidate stale permits/receipts/grants by bumping the epoch on any widening/restriction.
        if (report.RequiresEpochBump && _epoch is not null)
            _epoch.Bump();

        return Result<PolicySaveResult>.Ok(new PolicySaveResult(saveRes.Value!, report));
    }

    /// <summary>
    /// Previews the impact of a proposed edit WITHOUT persisting it, so the UI can show the diff and
    /// the second-confirmation prompt before committing. The actual <see cref="SavePolicyAsync"/>
    /// re-runs the gate (never trusts the preview alone). Returns a failure when the target set is too
    /// large to analyze completely, mirroring the save-time guard.
    /// </summary>
    public async Task<Result<PolicyImpactReport>> PreviewImpactAsync(
        PolicyLayer editedLayer,
        string? scopeId,
        IReadOnlyList<PolicyStatement> newStatements,
        IReadOnlyList<ImpactTarget> targets,
        IClock clock,
        CancellationToken ct = default)
    {
        foreach (var s in newStatements)
        {
            var v = s.Validate();
            if (!v.IsSuccess)
                return Result<PolicyImpactReport>.Fail(v.Error!);
        }

        var docRes = await _store.GetCurrentAsync(editedLayer, scopeId, ct);
        if (!docRes.IsSuccess)
            return Result<PolicyImpactReport>.Fail(docRes.Error!);

        var ctx = NeutralContext();
        var built = await PolicySnapshotBuilder.BuildAsync(_store, ctx, ct);
        var newSnapshot = BuildNewSnapshot(built.Snapshot, editedLayer, newStatements);

        return await _impact.AnalyzeAsync(
            built.Snapshot, newSnapshot, targets, clock.UtcNow, built.PresentLayers,
            queuedRunCount: 0, pendingApprovalCount: 0, ct);
    }

    private static PolicySnapshot BuildNewSnapshot(
        PolicySnapshot oldSnapshot, PolicyLayer editedLayer, IReadOnlyList<PolicyStatement> newStatements)
    {
        var layered = oldSnapshot.Statements
            .Where(ls => ls.Layer != editedLayer)
            .Concat(newStatements.Select(s => new LayeredStatement(editedLayer, s)))
            .ToList();
        var hash = PolicyCanonicalizer.HashStatements(layered.Select(ls => ls.Statement));
        return new PolicySnapshot(hash, layered);
    }

    /// <summary>Neutral baseline context for snapshot building: no space link, healthy source.</summary>
    private static EvaluationContext NeutralContext() => new(
        PolicySubject.AutomationPrincipal, string.Empty, null, true, false, false, null,
        true, false, 0, false, DateTimeOffset.UnixEpoch, "interactive", "manual", 1, 0, "healthy");
}
