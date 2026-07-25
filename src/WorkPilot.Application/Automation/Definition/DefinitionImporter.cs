using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation;

namespace WorkPilot.Application.Automation.Definition;

/// <summary>Port: import a definition from a portable JSON, rebuilding all identifiers.</summary>
public interface IDefinitionImporter
{
    /// <summary>
    /// Parses + validates <paramref name="json"/> and creates a brand-new, independent automation. All
    /// identifiers (automation, revision, trigger, nodes) are rebuilt so the import can never collide
    /// with or resurrect the source (AUT-A07). The imported automation is created <c>Draft</c>
    /// (disabled); if the validation produced unresolved-source / unresolved-timezone warnings it is
    /// additionally flagged <c>NeedsReview</c> and the caller must review before enabling (AUT-A08).
    /// </summary>
    Task<Result<ImportedAutomation>> ImportAsync(string json, CancellationToken ct = default);
}

public sealed class DefinitionImporter : IDefinitionImporter
{
    private readonly IAutomationRepository _repo;
    private readonly IIdGenerator _ids;
    private readonly IClock _clock;

    public DefinitionImporter(IAutomationRepository repo, IIdGenerator ids, IClock clock)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<Result<ImportedAutomation>> ImportAsync(string json, CancellationToken ct = default)
    {
        var parsed = new DefinitionSchemaValidator().Validate(json);
        if (!parsed.IsSuccess) return Result<ImportedAutomation>.Fail(parsed.Error!);
        var def = parsed.Value!;

        var now = _clock.UtcNow;
        var newAutoId = AutomationId.Create(_ids);
        var newRevId = AutomationRevisionId.Create(_ids);
        var newTriggerId = _ids.NewId();

        // Rebuild node identifiers and remap edges / entry reference (AUT-A07: ID 重建).
        var nodeMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var n in def.Workflow.Nodes)
            nodeMap[n.NodeId] = _ids.NewId();

        var newTrigger = def.Trigger with { TriggerId = newTriggerId };
        var newWorkflow = RebuildWorkflow(def.Workflow, nodeMap);
        var newBinding = new AutomationBinding(def.ProjectId, def.ExpertId);
        var newPermission = def.PermissionRequest;

        AutomationRevision revision;
        try
        {
            revision = AutomationRevision.Create(
                newRevId, newAutoId, 1, newTrigger, newWorkflow, newBinding,
                def.Budget, def.OverlapPolicy, def.MissedRunPolicy, newPermission, now);
        }
        catch (Exception ex) when (ex is DomainException or FormatException or ArgumentException)
        {
            return Result<ImportedAutomation>.Fail(AutomationErrors.DefinitionImportFailedError(ex.Message));
        }

        var create = AutomationDefinition.Create(newAutoId, def.SpaceId, def.Name, def.Description, newRevId, now);
        if (!create.IsSuccess) return Result<ImportedAutomation>.Fail(create.Error!);
        var automation = create.Value!; // lifecycle = Draft (disabled)

        var saved = await _repo.SaveAsync(automation, revision, ct).ConfigureAwait(false);
        if (!saved.IsSuccess) return Result<ImportedAutomation>.Fail(saved.Error!);

        return Result<ImportedAutomation>.Ok(new ImportedAutomation(
            newAutoId, newRevId, def.NeedsReview, def.Warnings, now));
    }

    private static WorkflowDefinition RebuildWorkflow(WorkflowDefinition source, Dictionary<string, string> nodeMap)
    {
        var nodes = source.Nodes.Select(n => n with { NodeId = nodeMap[n.NodeId] }).ToList();
        var edges = source.Edges
            .Select(e => new WorkflowEdge(nodeMap[e.FromNodeId], nodeMap[e.ToNodeId], e.Branch))
            .ToList();
        var entry = nodeMap[source.EntryNodeId];
        return new WorkflowDefinition(source.SchemaVersion, entry, nodes, edges);
    }
}
