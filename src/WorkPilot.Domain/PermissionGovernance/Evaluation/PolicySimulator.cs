using System.Collections.Generic;

namespace WorkPilot.Domain.PermissionGovernance.Evaluation;

/// <summary>
/// Policy simulator (doc 07 §14). It intentionally shares the SAME pure <see cref="PolicyEvaluator"/>
/// as the real gate — there is no "simplified simulation algorithm" — so simulator output and
/// production decisions are guaranteed consistent. The only difference from a real evaluation is
/// what the caller does with the result: the simulator must NOT issue a receipt/permit and must NOT
/// write a formal policy audit entry; it records only a local <c>PolicySimulationExecuted</c> marker.
/// </summary>
public static class PolicySimulator
{
    public static PermissionDecision Simulate(
        PolicySnapshot snapshot,
        EvaluationContext context,
        CapabilityDescriptor capability,
        EvaluationArguments arguments,
        IReadOnlyList<PolicyLayer>? presentLayers = null)
        => PolicyEvaluator.Evaluate(snapshot, context, capability, arguments, presentLayers);
}
