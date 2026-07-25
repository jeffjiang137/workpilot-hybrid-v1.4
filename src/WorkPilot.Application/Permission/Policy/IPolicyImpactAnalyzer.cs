using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Domain.PermissionGovernance;
using WorkPilot.Domain.PermissionGovernance.Evaluation;

namespace WorkPilot.Application.Permission.Policy;

/// <summary>
/// Pre-save impact analysis port (PER-008, doc 07 §15). Implemented by
/// <see cref="PolicyImpactService"/>. Abstracted so the admin save gate can be tested deterministically
/// while the real analyzer reuses the pure <see cref="PolicyImpactAnalyzer"/> and the same evaluator
/// the gate shares with the simulator (doc 07 §14).
/// </summary>
public interface IPolicyImpactAnalyzer
{
    /// <summary>
    /// Computes the impact of moving from <paramref name="oldSnapshot"/> to <paramref name="newSnapshot"/>
    /// across <paramref name="targets"/>. Returns a failure (e.g. <c>POLICY_IMPACT_INCOMPLETE</c>) when
    /// the target set exceeds <see cref="Limits.V1_5.MaxImpactAnalysisTargets"/> and results would be
    /// incomplete, in which case saving must be blocked.
    /// </summary>
    Task<Result<PolicyImpactReport>> AnalyzeAsync(
        PolicySnapshot oldSnapshot,
        PolicySnapshot newSnapshot,
        IReadOnlyList<ImpactTarget> targets,
        DateTimeOffset nowUtc,
        IReadOnlyList<PolicyLayer>? presentLayers = null,
        int queuedRunCount = 0,
        int pendingApprovalCount = 0,
        CancellationToken ct = default);
}
