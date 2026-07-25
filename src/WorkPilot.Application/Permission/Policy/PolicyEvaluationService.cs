using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Domain.PermissionGovernance.Evaluation;

namespace WorkPilot.Application.Permission.Policy;

/// <summary>
/// Orchestrates a real policy decision (doc 07 §6/§13): loads the current immutable snapshot, looks
/// up the bounded decision cache, and — on a miss — runs the pure <see cref="PolicyEvaluator"/> and
/// caches the result. The same evaluator is shared by the simulator (doc 07 §14) and the native
/// final gate (doc 07 §10/§11), so there is a single source of truth for decisions.
/// </summary>
public sealed class PolicyEvaluationService
{
    private readonly IPolicyStore _store;
    private readonly PolicyEvaluationCache _cache;

    public PolicyEvaluationService(IPolicyStore store, PolicyEvaluationCache cache)
    {
        _store = store;
        _cache = cache;
    }

    public async Task<PermissionDecision> EvaluateAsync(
        EvaluationContext context,
        CapabilityDescriptor capability,
        EvaluationArguments arguments,
        CancellationToken ct = default)
    {
        var built = await PolicySnapshotBuilder.BuildAsync(_store, context, ct);
        var key = CacheKey(built.Snapshot.PolicyHash, capability, context);

        if (_cache.TryGet(key, context.NowUtc, out var cached))
            return cached;

        var decision = PolicyEvaluator.Evaluate(built.Snapshot, context, capability, arguments, built.PresentLayers);
        _cache.Set(key, decision, context.NowUtc);
        return decision;
    }

    /// <summary>Call after any policy save/grant/revoke/source/schema/epoch change (doc 07 §13).</summary>
    public void InvalidateCache() => _cache.InvalidateAll();

    private static string CacheKey(string policyHash, CapabilityDescriptor cap, EvaluationContext ctx)
        => $"{policyHash}|{cap.StableId}|{cap.SchemaSha256 ?? "-"}|{ctx.InvariantKey()}";
}
