using System;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Domain.PermissionGovernance.Evaluation;

namespace WorkPilot.Application.Permission.Policy;

/// <summary>Local metadata recorded when a simulation runs (doc 07 §14). Simulations must NOT issue a
/// receipt/permit and must NOT write a formal policy audit entry — only this local marker.</summary>
public sealed record PolicySimulationExecuted(
    DateTimeOffset OccurredAtUtc,
    string CapabilityStableId,
    PermissionDecisionKind Result);

/// <summary>
/// Runs a policy simulation. It shares the exact pure <see cref="PolicyEvaluator"/> used by the real
/// gate (doc 07 §14) — no simplified simulation algorithm — but deliberately does NOT consult the
/// decision cache, does NOT issue a permit/receipt, and does NOT write a formal audit row. It only
/// records a local <see cref="PolicySimulationExecuted"/> marker via <see cref="_onRecorded"/>.
/// </summary>
public sealed class PolicySimulatorService
{
    private readonly IPolicyStore _store;
    private readonly Action<PolicySimulationExecuted>? _onRecorded;

    public PolicySimulatorService(IPolicyStore store, Action<PolicySimulationExecuted>? onRecorded = null)
    {
        _store = store;
        _onRecorded = onRecorded;
    }

    public async Task<PermissionDecision> SimulateAsync(
        EvaluationContext context,
        CapabilityDescriptor capability,
        EvaluationArguments arguments,
        CancellationToken ct = default)
    {
        var built = await PolicySnapshotBuilder.BuildAsync(_store, context, ct);
        var decision = PolicySimulator.Simulate(built.Snapshot, context, capability, arguments, built.PresentLayers);
        _onRecorded?.Invoke(new PolicySimulationExecuted(
            context.NowUtc, capability.StableId, decision.Kind));
        return decision;
    }
}
