using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Domain.PermissionGovernance.Evaluation;

namespace WorkPilot.Application.Permission.Policy;

/// <summary>
/// Managed stand-in for the Native Final Gate (doc 07 §10/§11, T12 <c>ManagedPermitCore</c> pattern).
/// It performs the SAME pure evaluation as the real gate — always fresh, never consulting the decision
/// cache, so the Current-State Check (doc 07 §11) is authoritative — and maps the result to a
/// <see cref="GateResult"/>. It does NOT construct a native <c>ExecutionPermit</c>: on Windows a
/// <c>NativePolicyGate</c> P/Invokes <c>wp_permit_issue</c> / <c>wp_permit_consume_and_check</c> and
/// fills <see cref="GateResult.PermitToken"/>. Until that native build, governance must not be claimed
/// complete (doc 07 §10).
/// </summary>
public sealed class ManagedPolicyGate : INativePolicyGate
{
    private readonly IPolicyStore _store;

    public ManagedPolicyGate(IPolicyStore store) => _store = store;

    public async Task<GateResult> CheckAsync(
        EvaluationContext context,
        CapabilityDescriptor capability,
        EvaluationArguments arguments,
        CancellationToken ct = default)
    {
        // Fresh evaluation (no cache): the final gate's Current-State Check must not trust cached state.
        var built = await PolicySnapshotBuilder.BuildAsync(_store, context, ct);
        var decision = PolicyEvaluator.Evaluate(built.Snapshot, context, capability, arguments, built.PresentLayers);

        // Native impl: if decision.IsAllow, P/Invoke wp_permit_issue and set PermitToken here.
        string? permitToken = null;
        return new GateResult(
            decision.Kind, decision.PrimaryReasonCode, decision.EffectiveRisk,
            decision.EffectiveScope, decision.PolicyHash, permitToken, decision.Trace);
    }
}
