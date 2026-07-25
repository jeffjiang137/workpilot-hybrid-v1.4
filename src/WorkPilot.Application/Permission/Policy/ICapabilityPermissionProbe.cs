using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Domain.PermissionGovernance.Evaluation;

namespace WorkPilot.Application.Permission.Policy;

/// <summary>
/// Port used by the enable Preflight (AUT-005 / AUT-A10) to ask the SAME pure policy evaluator
/// the real gate and simulator use (doc 07 §14) whether a SET of candidate capabilities would be
/// allowed. <see cref="PolicyProjectionService"/> is the production implementation; the Preflight
/// depends on this interface (not the concrete class) so it can be exercised with a fake projection
/// in platform-independent tests.
/// </summary>
public interface ICapabilityPermissionProbe
{
    Task<IReadOnlyList<EffectiveCapabilityView>> ProjectAsync(
        EvaluationContext context,
        IReadOnlyList<CapabilityQuery> queries,
        CancellationToken ct = default);
}
