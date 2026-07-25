using System.Collections.Generic;
using WorkPilot.Contracts.Primitives.Ids;

namespace WorkPilot.Domain.Automation;

/// <summary>A single field-level difference between two revisions (AUT-007: history + diff).</summary>
public sealed record FieldChange(string Path, string? From, string? To);

/// <summary>Structured diff between two immutable revisions.</summary>
public sealed record RevisionDiff(
    AutomationRevisionId FromRevisionId,
    AutomationRevisionId ToRevisionId,
    IReadOnlyList<FieldChange> Changes)
{
    public bool HasChanges => Changes.Count > 0;

    public static RevisionDiff Compute(AutomationRevision from, AutomationRevision to)
    {
        var changes = new List<FieldChange>();
        if (from.Trigger.Type != to.Trigger.Type)
            changes.Add(new FieldChange("trigger.type", from.Trigger.Type.ToString(), to.Trigger.Type.ToString()));
        if (from.Workflow.Nodes.Count != to.Workflow.Nodes.Count)
            changes.Add(new FieldChange("workflow.node_count", from.Workflow.Nodes.Count.ToString(), to.Workflow.Nodes.Count.ToString()));
        if (from.Budget.MaxModelTurns != to.Budget.MaxModelTurns)
            changes.Add(new FieldChange("budget.max_model_turns", from.Budget.MaxModelTurns.ToString(), to.Budget.MaxModelTurns.ToString()));
        if (from.Budget.MaxTotalTokens != to.Budget.MaxTotalTokens)
            changes.Add(new FieldChange("budget.max_total_tokens", from.Budget.MaxTotalTokens.ToString(), to.Budget.MaxTotalTokens.ToString()));
        if (from.PermissionRequest.Scope != to.PermissionRequest.Scope)
            changes.Add(new FieldChange("permission_request.scope", from.PermissionRequest.Scope, to.PermissionRequest.Scope));
        if (from.Binding.ExpertId != to.Binding.ExpertId)
            changes.Add(new FieldChange("binding.expert_id", from.Binding.ExpertId, to.Binding.ExpertId));
        if (from.Binding.ProjectId != to.Binding.ProjectId)
            changes.Add(new FieldChange("binding.project_id", from.Binding.ProjectId, to.Binding.ProjectId));
        if (from.OverlapPolicy != to.OverlapPolicy)
            changes.Add(new FieldChange("overlap_policy", from.OverlapPolicy.ToStorage(), to.OverlapPolicy.ToStorage()));
        if (from.MissedRunPolicy != to.MissedRunPolicy)
            changes.Add(new FieldChange("missed_run_policy", from.MissedRunPolicy.ToStorage(), to.MissedRunPolicy.ToStorage()));
        return new RevisionDiff(from.Id, to.Id, changes);
    }
}

/// <summary>
/// Impact analysis result (T04 DoD: 影响分析). Revision history and a field-level diff are always
/// available; run-reference checking becomes available once the run tables exist (T07).
/// </summary>
public sealed record ImpactAnalysis(
    AutomationId AutomationId,
    int RevisionCount,
    AutomationRevisionId CurrentRevisionId,
    bool RunReferenceCheckAvailable,
    int RunsReferencingCurrentRevision,
    IReadOnlyList<RevisionDiff> RecentDiffs,
    IReadOnlyList<string> Notes)
{
    public bool HasUnpublishedChanges =>
        RecentDiffs.Count > 0 && RecentDiffs[^1].HasChanges;
}
