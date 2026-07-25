using System;
using System.Collections.Generic;
using System.Text;
using WorkPilot.Contracts.Primitives.Ids;

namespace WorkPilot.Domain.PermissionGovernance.Evaluation;

/// <summary>
/// Runtime, non-argument context for an evaluation (doc 07 §6 steps 1–4, §11). Carries the source/
/// space/epoch/emergency state plus the time and condition environment used by time-window and
/// run-mode conditions. <see cref="NowUtc"/> is intentionally NOT part of <see cref="InvariantKey"/>
/// so the evaluation cache (doc 07 §13) can key on the stable subset and rely on TTL for time drift.
/// </summary>
public sealed record EvaluationContext(
    PolicySubject Subject,
    string SourceStableId,
    string? SourceSchemaSha256,
    bool SourceEnabled,
    bool SourceQuarantined,
    bool SpaceLinked,
    string? SpaceId,
    bool ExpertGranted,
    bool EmergencyStopActive,
    long CurrentEpoch,
    bool AutomationGrantPresent,
    DateTimeOffset NowUtc,
    string RunMode,
    string TriggerType,
    int TargetCount,
    long ResultSize,
    string SourceHealth)
{
    /// <summary>Stable, time-independent key fragment for cache keys (doc 07 §13).</summary>
    public string InvariantKey()
    {
        var b = new StringBuilder();
        b.Append(nameof(PolicySubject)).Append('=').Append(Subject).Append(';');
        b.Append("src=").Append(SourceStableId).Append(';');
        b.Append("sschema=").Append(SourceSchemaSha256 ?? "-").Append(';');
        b.Append("sen=").Append(SourceEnabled).Append(';');
        b.Append("sq=").Append(SourceQuarantined).Append(';');
        b.Append("space=").Append(SpaceLinked).Append(';');
        b.Append("spaceId=").Append(SpaceId ?? "-").Append(';');
        b.Append("exp=").Append(ExpertGranted).Append(';');
        b.Append("emerg=").Append(EmergencyStopActive).Append(';');
        b.Append("epoch=").Append(CurrentEpoch).Append(';');
        b.Append("grant=").Append(AutomationGrantPresent).Append(';');
        b.Append("mode=").Append(RunMode).Append(';');
        b.Append("trig=").Append(TriggerType).Append(';');
        b.Append("tc=").Append(TargetCount).Append(';');
        b.Append("rs=").Append(ResultSize).Append(';');
        b.Append("health=").Append(SourceHealth).Append(';');
        return b.ToString();
    }
}

/// <summary>What the capability definer declares about a capability (doc 07 §4/§5).</summary>
public sealed record CapabilityDescriptor(
    string StableId,
    string SchemaSha256,
    RiskLevel LocalRisk,
    ResourceScope? ManifestScope);

/// <summary>Validated invocation arguments: the scope the call actually targets and its derived risk.</summary>
public sealed record EvaluationArguments(
    ResourceScope? InvocationScope,
    RiskLevel ArgumentRisk)
{
    public static readonly EvaluationArguments Empty = new(null, RiskLevel.Low);
}

/// <summary>A statement tagged with the layer its document belongs to (the evaluator needs the layer
/// to enforce required-layer coverage, doc 07 §6 step 9).</summary>
public sealed record LayeredStatement(PolicyLayer Layer, PolicyStatement Statement);

/// <summary>An immutable, point-in-time view of all enabled policy statements across layers, plus the
/// canonical hash of the whole set (used as cache key fragment and embedded in run snapshots).</summary>
public sealed record PolicySnapshot(
    string PolicyHash,
    IReadOnlyList<LayeredStatement> Statements)
{
    public static PolicySnapshot FromLayers(
        string policyHash,
        IEnumerable<(PolicyLayer Layer, IReadOnlyList<PolicyStatement> Statements)> layers)
    {
        var all = new List<LayeredStatement>();
        foreach (var (layer, stmts) in layers)
            foreach (var s in stmts)
                all.Add(new LayeredStatement(layer, s));
        return new PolicySnapshot(policyHash, all);
    }
}
