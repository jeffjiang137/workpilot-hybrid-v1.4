using System.Text.Json.Nodes;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation;
using WorkPilot.Domain.Automation.Run;

namespace WorkPilot.Application.Automation.Materialization;

/// <summary>
/// Builds the immutable <see cref="RunSnapshot"/> that freezes everything a run needs, so execution
/// never re-reads the live definition (RUN-003, spec §1 principle "先冻结后执行"). Pure (no I/O): the
/// caller supplies the revision, the frozen expert revision id, and the revocation epoch; the
/// canonical SHA-256 is computed over the frozen content via <see cref="JcsCanonicalizer"/> so the
/// same revision always yields the same hash. The revocation epoch is supplied by the caller — in
/// v1.5 it defaults to 0 until the revocation service (T13/SEC) is wired in.
/// </summary>
public static class RunSnapshotFactory
{
    public static RunSnapshot Build(
        IIdGenerator idGenerator,
        AutomationRevision revision,
        ExpertRevisionId expertRevisionId,
        int revocationEpoch,
        DateTimeOffset now)
    {
        var policyNode = revision.PermissionRequest.ToCanonicalJson();
        var capabilityNode = BuildCapabilitySnapshot(revision.Workflow);
        var workflowNode = revision.Workflow.ToCanonicalJson();
        var bindingNode = revision.Binding.ToCanonicalJson();
        var budgetNode = revision.Budget.ToCanonicalJson();

        var algo = new JsonObject
        {
            ["scheduler"] = ContractVersions.SchedulerAlgorithm,
            ["permission"] = ContractVersions.PermissionAlgorithm,
            ["redaction"] = ContractVersions.RedactionAlgorithm,
            ["audit"] = ContractVersions.AuditIntegrityAlgorithm
        };

        var content = new JsonObject
        {
            ["automation_revision_id"] = revision.Id.Value,
            ["expert_revision_id"] = expertRevisionId.Value,
            ["policy"] = policyNode,
            ["capability"] = capabilityNode,
            ["workflow"] = workflowNode,
            ["binding"] = bindingNode,
            ["budget"] = budgetNode,
            ["revocation_epoch"] = revocationEpoch,
            ["algorithm_versions"] = algo
        };

        var canonical = JcsCanonicalizer.CanonicalizeToSha256(content);

        return RunSnapshot.Create(
            RunSnapshotId.Create(idGenerator),
            revision.Id,
            expertRevisionId,
            policyNode.ToJsonString(),
            capabilityNode.ToJsonString(),
            workflowNode.ToJsonString(),
            bindingNode.ToJsonString(),
            budgetNode.ToJsonString(),
            revocationEpoch,
            algo.ToJsonString(),
            canonical,
            now);
    }

    /// <summary>
    /// Derives a stable capability snapshot from the workflow's capability nodes. Workflow nodes of
    /// kind "capability" expose a <c>capability_stable_id</c> in their payload; we freeze that list
    /// so execution cannot drift from what was approved. Nodes without a capability id are skipped.
    /// </summary>
    private static JsonNode BuildCapabilitySnapshot(WorkflowDefinition workflow)
    {
        var array = new JsonArray();
        foreach (var node in workflow.Nodes)
        {
            if (!string.Equals(node.Kind, "capability", System.StringComparison.OrdinalIgnoreCase))
                continue;
            var stableId = node.Payload?["capability_stable_id"]?.GetValue<string>();
            if (string.IsNullOrEmpty(stableId))
                continue;
            array.Add(new JsonObject
            {
                ["node_id"] = node.NodeId,
                ["capability_stable_id"] = stableId
            });
        }
        return array;
    }
}
