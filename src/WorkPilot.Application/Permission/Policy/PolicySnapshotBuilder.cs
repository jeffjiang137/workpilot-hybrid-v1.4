using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Domain.PermissionGovernance;
using WorkPilot.Domain.PermissionGovernance.Evaluation;

namespace WorkPilot.Application.Permission.Policy;

/// <summary>
/// Builds an immutable <see cref="PolicySnapshot"/> for evaluation from the current policy bundles
/// (T16 <see cref="IPolicyStore"/>). It loads the layers that have a stable document:
/// <see cref="PolicyLayer.BuiltInSafety"/>, <see cref="PolicyLayer.GlobalPolicy"/>, and
/// <see cref="PolicyLayer.SpacePolicy"/> (when a space is linked). Expert/Automation policy documents
/// are provisioned by the policy admin (T18) with explicit scope ids and loaded there; until then the
/// evaluator still treats Expert/Automation as always-required (doc 07 §6), so their absence yields
/// Ask (interactive) / Deny (automation) rather than fail-open — never a silent Allow.
/// </summary>
internal static class PolicySnapshotBuilder
{
    public sealed record Built(PolicySnapshot Snapshot, IReadOnlyList<PolicyLayer> PresentLayers);

    public static async Task<Built> BuildAsync(
        IPolicyStore store, EvaluationContext context, CancellationToken ct)
    {
        var requests = new List<(PolicyLayer Layer, string? ScopeId)>
        {
            (PolicyLayer.BuiltInSafety, null),
            (PolicyLayer.GlobalPolicy, null)
        };
        if (context.SpaceLinked && !string.IsNullOrEmpty(context.SpaceId))
            requests.Add((PolicyLayer.SpacePolicy, context.SpaceId));

        var layered = new List<LayeredStatement>();
        var present = new List<PolicyLayer>();
        var all = new List<PolicyStatement>();

        foreach (var (layer, scopeId) in requests)
        {
            var bundle = await store.GetCurrentAsync(layer, scopeId, ct);
            if (!bundle.IsSuccess)
                continue; // document not bootstrapped yet → layer not present
            var b = bundle.Value!;
            present.Add(layer);
            foreach (var s in b.Statements)
            {
                layered.Add(new LayeredStatement(layer, s));
                all.Add(s);
            }
        }

        var hash = all.Count == 0
            ? PolicyCanonicalizer.HashStatements(Enumerable.Empty<PolicyStatement>())
            : PolicyCanonicalizer.HashStatements(all);
        return new Built(new PolicySnapshot(hash, layered), present);
    }
}
