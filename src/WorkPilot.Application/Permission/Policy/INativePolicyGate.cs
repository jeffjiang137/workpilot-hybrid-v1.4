using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.PermissionGovernance;
using WorkPilot.Domain.PermissionGovernance.Evaluation;

namespace WorkPilot.Application.Permission.Policy;

/// <summary>
/// Result of the Native Final Gate (doc 07 §10/§11). For an Allow, a real native implementation
/// P/Invokes <c>wp_permit_issue</c> and returns a process-bound, single-use <see cref="PermitToken"/>;
/// C# must never construct the permit, so the managed stand-in returns <see langword="null"/>.
/// </summary>
public sealed record GateResult(
    PermissionDecisionKind Kind,
    string PrimaryReasonCode,
    RiskLevel EffectiveRisk,
    ResourceScope? EffectiveScope,
    string PolicyHash,
    string? PermitToken,
    IReadOnlyList<DecisionTraceItem> Trace);

/// <summary>
/// The Native Final Gate port (doc 07 §10/§11). The gate re-validates the current state and — only
/// for a positive decision — requests an execution permit from the Native C++ Core. Both the
/// background Host and the App reach the policy core through this same ABI, so no UI, host, connector,
/// or MCP can bypass it. Implemented on Windows by a P/Invoke adapter over <c>wp_permit_*</c>; the
/// managed stand-in (<see cref="ManagedPolicyGate"/>) performs the identical evaluation without a
/// native permit and is used for platform-independent verification.
/// </summary>
public interface INativePolicyGate
{
    Task<GateResult> CheckAsync(
        EvaluationContext context,
        CapabilityDescriptor capability,
        EvaluationArguments arguments,
        CancellationToken ct = default);
}
