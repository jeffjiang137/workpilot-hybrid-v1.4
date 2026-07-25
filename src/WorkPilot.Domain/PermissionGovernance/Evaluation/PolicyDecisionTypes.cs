using System;
using System.Collections.Generic;
using WorkPilot.Contracts.Primitives.Ids;

namespace WorkPilot.Domain.PermissionGovernance.Evaluation;

/// <summary>Final decision kind (doc 07 §2). <see cref="Defer"/> means policy allows but a time
/// window / rate / budget temporarily blocks execution.</summary>
public enum PermissionDecisionKind : int
{
    Allow = 0,
    Ask = 1,
    Deny = 2,
    Defer = 3
}

/// <summary>
/// One deterministic step of the evaluation (doc 07 §6). Trace ordering is fixed by step number,
/// then layer, then statement id; same input + same immutable snapshot yields a byte-identical trace
/// (time/nonce excluded). Enables audit drill-down and the security-center trace view (T20).
/// </summary>
public sealed record DecisionTraceItem(
    int Step,
    PolicyLayer? Layer,
    PolicyStatementId? StatementId,
    string ReasonCode,
    string Detail)
{
    public string ToStableLine()
        => $"{Step}|{Layer?.ToString() ?? "-"}|{StatementId?.Value ?? "-"}|{ReasonCode}|{Detail}";
}

/// <summary>
/// The complete, deterministic result of a policy evaluation (doc 07 §2). <see cref="PolicyHash"/>
/// is the canonical hash of the immutable snapshot used, so it can be embedded in run snapshots
/// (doc 07 §13) and later verified. <c>Permit</c> issuance is the Native Final Gate's job (T17) and
/// is intentionally NOT constructed here — the managed gate returns <see cref="PermissionDecision"/>
/// plus the caller requests a permit via <see cref="INativePolicyGate"/>.
/// </summary>
public sealed record PermissionDecision(
    PermissionDecisionKind Kind,
    string PrimaryReasonCode,
    RiskLevel EffectiveRisk,
    ResourceScope? EffectiveScope,
    IReadOnlyList<DecisionTraceItem> Trace,
    DateTimeOffset? DeferUntilUtc,
    string PolicyHash)
{
    /// <summary>True only when the policy core positively permits the action now.</summary>
    public bool IsAllow => Kind == PermissionDecisionKind.Allow;
    /// <summary>True when the policy core needs interactive/manual approval (must NOT be skipped as Deny).</summary>
    public bool IsAsk => Kind == PermissionDecisionKind.Ask;
    public bool IsDeny => Kind == PermissionDecisionKind.Deny;
    public bool IsDefer => Kind == PermissionDecisionKind.Defer;

    /// <summary>Stable, ordered trace suitable for hashing / display.</summary>
    public string StableTrace()
        => string.Join("\n", Trace.Select(t => t.ToStableLine()));
}
