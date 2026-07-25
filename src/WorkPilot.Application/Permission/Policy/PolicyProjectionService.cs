using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Domain.PermissionGovernance;
using WorkPilot.Domain.PermissionGovernance.Evaluation;

namespace WorkPilot.Application.Permission.Policy;

/// <summary>A single capability the projection is asked about (doc 07 §14 / PER-003).</summary>
public sealed record CapabilityQuery(
    string StableId,
    /// <summary>Source schema hash. The evaluator's Step 4 compares this to the context's source schema
    /// (both are the source schema), so a current capability must pass it or be denied as drifted.</summary>
    string SourceSchemaSha256,
    RiskLevel ArgumentRisk,
    ResourceScope? InvocationScope);

/// <summary>Effective permission for one capability under the projected context (PER-003 有效权限查看).</summary>
public sealed record EffectiveCapabilityView(
    string CapabilityStableId,
    PermissionDecisionKind Decision,
    RiskLevel EffectiveRisk,
    ResourceScope? EffectiveScope,
    string PrimaryReasonCode,
    IReadOnlyList<DecisionTraceItem> Trace);

/// <summary>
/// Effective-permission projection (PER-003): given a subject/source/automation-revision context, it
/// evaluates a SET of candidate capabilities with the SAME pure <see cref="PolicyEvaluator"/> the real
/// gate and simulator use — never a simplified projection algorithm (doc 07 §14) — and returns the
/// effective decision, risk, and scope for each. It reuses <see cref="PolicySimulatorService"/> so every
/// row is a faithful simulation (shares the evaluator, records only local metadata, issues no permit),
/// providing the backend for the permission page's "what can this automation actually do" view.
/// </summary>
public sealed class PolicyProjectionService : ICapabilityPermissionProbe
{
    private readonly PolicySimulatorService _simulator;

    public PolicyProjectionService(PolicySimulatorService simulator) => _simulator = simulator;

    public async Task<IReadOnlyList<EffectiveCapabilityView>> ProjectAsync(
        EvaluationContext context,
        IReadOnlyList<CapabilityQuery> queries,
        CancellationToken ct = default)
    {
        var views = new List<EffectiveCapabilityView>(queries.Count);
        foreach (var q in queries)
        {
            var cap = new CapabilityDescriptor(q.StableId, q.SourceSchemaSha256, q.ArgumentRisk, q.InvocationScope);
            var args = new EvaluationArguments(q.InvocationScope, q.ArgumentRisk);
            var dec = await _simulator.SimulateAsync(context, cap, args, ct);
            views.Add(new EffectiveCapabilityView(
                q.StableId, dec.Kind, dec.EffectiveRisk, dec.EffectiveScope, dec.PrimaryReasonCode, dec.Trace));
        }

        return views;
    }
}
