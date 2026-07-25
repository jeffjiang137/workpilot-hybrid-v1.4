using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Domain.PermissionGovernance;
using WorkPilot.Domain.PermissionGovernance.Evaluation;

namespace WorkPilot.Application.Permission.Policy;

/// <summary>
/// Application service for pre-save impact analysis (PER-008). Wraps the pure
/// <see cref="PolicyImpactAnalyzer"/> and enriches the report with store-backed counts (active
/// grants affected by privilege expansion; caller-supplied queued runs / pending approvals). Saving
/// is blocked when the target set exceeds <see cref="Limits.V1_5.MaxImpactAnalysisTargets"/> (results
/// must be complete, doc 07 §15). Privilege expansion forces a second confirmation upstream.
/// </summary>
public sealed class PolicyImpactService : IPolicyImpactAnalyzer
{
    private readonly IGrantStore? _grants;

    public PolicyImpactService(IGrantStore? grants = null) => _grants = grants;

    public async Task<Result<PolicyImpactReport>> AnalyzeAsync(
        PolicySnapshot oldSnapshot,
        PolicySnapshot newSnapshot,
        IReadOnlyList<ImpactTarget> targets,
        DateTimeOffset nowUtc,
        IReadOnlyList<PolicyLayer>? presentLayers = null,
        int queuedRunCount = 0,
        int pendingApprovalCount = 0,
        CancellationToken ct = default)
    {
        if (targets.Count > Limits.V1_5.MaxImpactAnalysisTargets)
            return Result<PolicyImpactReport>.Fail(PolicyErrors.ImpactIncompleteError(targets.Count));

        var report = PolicyImpactAnalyzer.Analyze(oldSnapshot, newSnapshot, targets, presentLayers);

        var grantCount = 0;
        if (_grants is not null)
        {
            foreach (var (target, impact) in targets.Zip(report.Targets, (t, i) => (t, i)))
            {
                if (!impact.IsPrivilegeExpansion)
                    continue;
                var active = await _grants.ListActiveGrantsAsync(
                    target.CapabilityStableId, target.SourceKind, target.SourceStableId,
                    target.CapabilitySchemaSha256, nowUtc, ct);
                if (active.IsSuccess)
                    grantCount += active.Value!.Count;
            }
        }

        return Result<PolicyImpactReport>.Ok(report with
        {
            AffectedGrantCount = grantCount,
            QueuedRunCount = queuedRunCount,
            PendingApprovalCount = pendingApprovalCount
        });
    }
}
