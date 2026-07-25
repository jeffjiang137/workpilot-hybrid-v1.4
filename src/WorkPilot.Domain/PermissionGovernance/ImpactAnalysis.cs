using System;
using System.Collections.Generic;
using System.Linq;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.PermissionGovernance.Evaluation;

namespace WorkPilot.Domain.PermissionGovernance;

/// <summary>A concrete automation invocation to evaluate for policy-change impact (PER-008).</summary>
public sealed record ImpactTarget(
    string AutomationId,
    string SourceKind,
    string SourceStableId,
    string? SourceSchemaSha256,
    string CapabilityStableId,
    string CapabilitySchemaSha256,
    RiskLevel ArgumentRisk,
    ResourceScope? InvocationScope,
    string? SpaceId);

/// <summary>How a target's effective permission changes between the old and new policy (doc 07 §15).</summary>
public enum ImpactTransition : int
{
    Unchanged = 0,
    AllowToAsk = 1,
    AllowToDeny = 2,
    AskToAllow = 3,     // privilege expansion
    DenyToAllow = 4,    // privilege expansion
    AskToDeny = 5,
    DenyToAsk = 6
}

public sealed record TargetImpact(
    string AutomationId,
    PermissionDecisionKind OldDecision,
    PermissionDecisionKind NewDecision,
    ImpactTransition Transition,
    bool IsPrivilegeExpansion);

public sealed record StatementDiff(
    IReadOnlyList<PolicyStatementId> Added,
    IReadOnlyList<PolicyStatementId> Removed,
    IReadOnlyList<PolicyStatementId> Modified);

/// <summary>
/// Pre-save impact of a policy change (PER-008, doc 07 §15). Computed by re-evaluating every target
/// automation against the old and new <see cref="PolicySnapshot"/> with the surrounding context held
/// constant, so transitions reflect only the statement changes. Privilege expansion (Ask/Deny →
/// Allow) is highlighted and forces a second confirmation + epoch bump (to invalidate stale
/// permits/receipts/grants). Pure and I/O-free; the Application service supplies target enumeration
/// and store-backed grant/run/approval counts.
/// </summary>
public sealed record PolicyImpactReport(
    StatementDiff Statements,
    IReadOnlyList<TargetImpact> Targets,
    bool HasPrivilegeExpansion,
    bool RequiresEpochBump,
    int AffectedGrantCount,
    int QueuedRunCount,
    int PendingApprovalCount)
{
    /// <summary>True when the target set was fully analyzed (≤ <see cref="Limits.V1_5.MaxImpactAnalysisTargets"/>).</summary>
    public bool IsComplete(int targetCount) => targetCount <= Limits.V1_5.MaxImpactAnalysisTargets;
}

public static class PolicyImpactAnalyzer
{
    public static PolicyImpactReport Analyze(
        PolicySnapshot oldSnapshot,
        PolicySnapshot newSnapshot,
        IReadOnlyList<ImpactTarget> targets,
        IReadOnlyList<PolicyLayer>? presentLayers = null)
    {
        var statements = DiffStatements(oldSnapshot, newSnapshot);

        var impacts = new List<TargetImpact>();
        var hasExpansion = false;
        foreach (var t in targets)
        {
            var ctx = BuildContext(t);
            // The domain evaluator's Step 4 compares capability.SchemaSha256 against
            // context.SourceSchemaSha256 (both are the source schema); pass the source schema so a
            // current capability is not spuriously denied. The capability schema (t.CapabilitySchemaSha256)
            // is used separately by the Application service to count matching grants.
            var cap = new CapabilityDescriptor(t.CapabilityStableId, t.SourceSchemaSha256 ?? string.Empty, t.ArgumentRisk, t.InvocationScope);
            var args = new EvaluationArguments(t.InvocationScope, t.ArgumentRisk);

            var oldDec = PolicyEvaluator.Evaluate(oldSnapshot, ctx, cap, args, presentLayers);
            var newDec = PolicyEvaluator.Evaluate(newSnapshot, ctx, cap, args, presentLayers);
            var transition = Classify(oldDec.Kind, newDec.Kind);
            var expansion = transition is ImpactTransition.AskToAllow or ImpactTransition.DenyToAllow;
            if (expansion) hasExpansion = true;
            impacts.Add(new TargetImpact(t.AutomationId, oldDec.Kind, newDec.Kind, transition, expansion));
        }

        // Epoch bump when widening permissions, or when removing/denying previously-allowed access
        // (stale permits/receipts/grants must be invalidated, doc 07 §11/§15).
        var requiresEpoch = hasExpansion
            || impacts.Any(x => x.Transition is ImpactTransition.AllowToAsk or ImpactTransition.AllowToDeny);

        return new PolicyImpactReport(
            statements, impacts, hasExpansion, requiresEpoch,
            AffectedGrantCount: 0, QueuedRunCount: 0, PendingApprovalCount: 0);
    }

    private static StatementDiff DiffStatements(PolicySnapshot oldS, PolicySnapshot newS)
    {
        var oldIds = oldS.Statements.Select(s => s.Statement).ToDictionary(s => s.Id);
        var newIds = newS.Statements.Select(s => s.Statement).ToDictionary(s => s.Id);
        var added = new List<PolicyStatementId>();
        var removed = new List<PolicyStatementId>();
        var modified = new List<PolicyStatementId>();

        foreach (var id in newIds.Keys)
            if (!oldIds.ContainsKey(id)) added.Add(id);
        foreach (var id in oldIds.Keys)
            if (!newIds.ContainsKey(id)) removed.Add(id);
        foreach (var id in oldIds.Keys.Intersect(newIds.Keys))
            if (!StatementsEquivalent(oldIds[id], newIds[id])) modified.Add(id);

        return new StatementDiff(added, removed, modified);
    }

    private static bool StatementsEquivalent(PolicyStatement a, PolicyStatement b)
    {
        if (a.Enabled != b.Enabled || a.Effect != b.Effect || a.RiskMin != b.RiskMin
            || a.RiskMax != b.RiskMax || a.Priority != b.Priority) return false;
        if (!a.Subjects.SequenceEqual(b.Subjects)) return false;
        if (a.SourceSelectorJson != b.SourceSelectorJson) return false;
        if (a.CapabilitySelectorJson != b.CapabilitySelectorJson) return false;
        if (a.Scope?.ToStorageJson() != b.Scope?.ToStorageJson()) return false;
        if (a.Conditions.Count != b.Conditions.Count) return false;
        for (var i = 0; i < a.Conditions.Count; i++)
            if (a.Conditions[i].Kind != b.Conditions[i].Kind || a.Conditions[i].DetailJson != b.Conditions[i].DetailJson)
                return false;
        return true;
    }

    private static ImpactTransition Classify(PermissionDecisionKind oldK, PermissionDecisionKind newK)
    {
        if (oldK == newK) return ImpactTransition.Unchanged;
        return (oldK, newK) switch
        {
            (PermissionDecisionKind.Allow, PermissionDecisionKind.Ask) => ImpactTransition.AllowToAsk,
            (PermissionDecisionKind.Allow, PermissionDecisionKind.Deny) => ImpactTransition.AllowToDeny,
            (PermissionDecisionKind.Ask, PermissionDecisionKind.Allow) => ImpactTransition.AskToAllow,
            (PermissionDecisionKind.Deny, PermissionDecisionKind.Allow) => ImpactTransition.DenyToAllow,
            (PermissionDecisionKind.Ask, PermissionDecisionKind.Deny) => ImpactTransition.AskToDeny,
            (PermissionDecisionKind.Deny, PermissionDecisionKind.Ask) => ImpactTransition.DenyToAsk,
            _ => ImpactTransition.Unchanged
        };
    }

    /// <summary>
    /// Builds a constant evaluation context for a target so that only the policy statements differ
    /// between old/new evaluations. Source is assumed enabled/healthy and expert + grant are present;
    /// this isolates the effect of the saved statement changes (PER-008).
    /// </summary>
    private static EvaluationContext BuildContext(ImpactTarget t)
        => new(
            Subject: PolicySubject.AutomationPrincipal,
            SourceStableId: t.SourceStableId,
            SourceSchemaSha256: t.SourceSchemaSha256,
            SourceEnabled: true,
            SourceQuarantined: false,
            SpaceLinked: true,
            SpaceId: t.SpaceId,
            ExpertGranted: true,
            EmergencyStopActive: false,
            CurrentEpoch: 1,
            AutomationGrantPresent: true,
            NowUtc: DateTimeOffset.UnixEpoch,
            RunMode: "interactive",
            TriggerType: "manual",
            TargetCount: 1,
            ResultSize: 0,
            SourceHealth: "healthy");
}
