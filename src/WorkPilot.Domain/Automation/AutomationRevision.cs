using System.Text.Json.Nodes;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;

namespace WorkPilot.Domain.Automation;

/// <summary>
/// An immutable automation revision. Once saved, its content never changes; the
/// <see cref="CanonicalSha256"/> is a deterministic fingerprint of the canonicalized content
/// (spec §1.2). Use <see cref="Create"/> to compute the hash, or the record constructor directly
/// when reconstructing from storage.
/// </summary>
public sealed record AutomationRevision(
    AutomationRevisionId Id,
    AutomationId AutomationId,
    int RevisionNumber,
    TriggerDefinition Trigger,
    WorkflowDefinition Workflow,
    AutomationBinding Binding,
    RunBudget Budget,
    OverlapPolicy OverlapPolicy,
    MissedRunPolicy MissedRunPolicy,
    PermissionRequest PermissionRequest,
    string CanonicalSha256,
    DateTimeOffset CreatedAtUtc)
{
    public static AutomationRevision Create(
        AutomationRevisionId id,
        AutomationId automationId,
        int revisionNumber,
        TriggerDefinition trigger,
        WorkflowDefinition workflow,
        AutomationBinding binding,
        RunBudget budget,
        OverlapPolicy overlap,
        MissedRunPolicy missed,
        PermissionRequest permission,
        DateTimeOffset createdAtUtc)
    {
        var content = new JsonObject
        {
            ["trigger"] = trigger.ToCanonicalJson(),
            ["workflow"] = workflow.ToCanonicalJson(),
            ["binding"] = binding.ToCanonicalJson(),
            ["budget"] = budget.ToCanonicalJson(),
            ["overlap_policy"] = overlap.ToStorage(),
            ["missed_run_policy"] = missed.ToStorage(),
            ["permission_request"] = permission.ToCanonicalJson()
        };
        var hash = JcsCanonicalizer.CanonicalizeToSha256(content);
        return new AutomationRevision(id, automationId, revisionNumber, trigger, workflow, binding,
            budget, overlap, missed, permission, hash, createdAtUtc);
    }
}
