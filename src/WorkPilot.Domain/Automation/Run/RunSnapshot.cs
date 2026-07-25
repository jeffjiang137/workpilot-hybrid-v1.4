using System;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;

namespace WorkPilot.Domain.Automation.Run;

/// <summary>
/// An immutable, frozen projection of everything a run needs to be reproducible and auditable
/// without re-reading the live definition: the revision it executed, the expert revision, the
/// policy/capability/workflow/binding/budget snapshots, the revocation epoch, the algorithm
/// versions, and a canonical SHA-256 over the frozen content (RUN-003). Construct via
/// <see cref="Create"/>; the canonical hash is supplied by the materializer (T09) so this value
/// object stays free of JSON canonicalization concerns.
/// </summary>
public sealed record RunSnapshot(
    RunSnapshotId Id,
    AutomationRevisionId AutomationRevisionId,
    ExpertRevisionId ExpertRevisionId,
    string PolicySnapshotJson,
    string CapabilitySnapshotJson,
    string WorkflowSnapshotJson,
    string BindingSnapshotJson,
    string BudgetSnapshotJson,
    int RevocationEpoch,
    string AlgorithmVersionsJson,
    string CanonicalSha256,
    DateTimeOffset CreatedAtUtc)
{
    public static RunSnapshot Create(
        RunSnapshotId id,
        AutomationRevisionId automationRevisionId,
        ExpertRevisionId expertRevisionId,
        string policySnapshotJson,
        string capabilitySnapshotJson,
        string workflowSnapshotJson,
        string bindingSnapshotJson,
        string budgetSnapshotJson,
        int revocationEpoch,
        string algorithmVersionsJson,
        string canonicalSha256,
        DateTimeOffset createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(policySnapshotJson))
            throw new DomainException(RunErrors.SnapshotJsonEmptyError("policy"));
        if (string.IsNullOrWhiteSpace(capabilitySnapshotJson))
            throw new DomainException(RunErrors.SnapshotJsonEmptyError("capability"));
        if (string.IsNullOrWhiteSpace(workflowSnapshotJson))
            throw new DomainException(RunErrors.SnapshotJsonEmptyError("workflow"));
        if (string.IsNullOrWhiteSpace(bindingSnapshotJson))
            throw new DomainException(RunErrors.SnapshotJsonEmptyError("binding"));
        if (string.IsNullOrWhiteSpace(budgetSnapshotJson))
            throw new DomainException(RunErrors.SnapshotJsonEmptyError("budget"));
        if (string.IsNullOrWhiteSpace(algorithmVersionsJson))
            throw new DomainException(RunErrors.SnapshotJsonEmptyError("algorithm_versions"));
        if (canonicalSha256.Length != 64)
            throw new DomainException(RunErrors.SnapshotCanonicalError());
        if (revocationEpoch < 0)
            throw new DomainException(RunErrors.InvalidRevocationEpochError());

        return new RunSnapshot(id, automationRevisionId, expertRevisionId, policySnapshotJson,
            capabilitySnapshotJson, workflowSnapshotJson, bindingSnapshotJson, budgetSnapshotJson,
            revocationEpoch, algorithmVersionsJson, canonicalSha256, createdAtUtc);
    }
}
