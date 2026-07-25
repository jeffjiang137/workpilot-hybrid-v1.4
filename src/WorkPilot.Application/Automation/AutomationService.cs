using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation;

namespace WorkPilot.Application.Automation;

public sealed record CreateAutomationRequest(
    SpaceId SpaceId, string Name, string Description,
    TriggerDefinition Trigger, WorkflowDefinition Workflow, AutomationBinding Binding,
    RunBudget Budget, OverlapPolicy OverlapPolicy, MissedRunPolicy MissedRunPolicy, PermissionRequest Permission);

public sealed record UpdateAutomationRequest(
    AutomationId Id, string Name, string Description, long ExpectedRowVersion,
    TriggerDefinition? Trigger, WorkflowDefinition? Workflow, AutomationBinding? Binding,
    RunBudget? Budget, OverlapPolicy? OverlapPolicy, MissedRunPolicy? MissedRunPolicy, PermissionRequest? Permission);

public interface IAutomationService
{
    Task<Result<AutomationDefinition>> CreateAsync(CreateAutomationRequest request, CancellationToken ct = default);
    Task<Result<AutomationDefinition>> GetAsync(AutomationId id, CancellationToken ct = default);
    /// <summary>Returns the immutable revision currently pointed at by the automation (needed by the editor to load content for edit).</summary>
    Task<Result<AutomationRevision>> GetCurrentRevisionAsync(AutomationId id, CancellationToken ct = default);
    Task<Result<IReadOnlyList<AutomationDefinition>>> ListBySpaceAsync(SpaceId spaceId, bool includeDeleted = false, CancellationToken ct = default);
    Task<Result<AutomationDefinition>> UpdateDraftAsync(UpdateAutomationRequest request, CancellationToken ct = default);
    Task<Result<AutomationDefinition>> PublishAsync(AutomationId id, AutomationRevisionId revisionId, long expectedRowVersion, CancellationToken ct = default);
    Task<Result<AutomationDefinition>> ArchiveAsync(AutomationId id, long expectedRowVersion, CancellationToken ct = default);
    Task<Result<AutomationDefinition>> SoftDeleteAsync(AutomationId id, long expectedRowVersion, CancellationToken ct = default);
    Task<Result<AutomationDefinition>> CopyAsync(AutomationId sourceId, SpaceId? targetSpaceId, CancellationToken ct = default);
    Task<Result<ImpactAnalysis>> AnalyzeImpactAsync(AutomationId id, CancellationToken ct = default);
}

/// <summary>
/// Orchestrates automation CRUD on top of <see cref="IAutomationRepository"/>. Produces immutable
/// revisions on every content edit (AUT-001), enforces optimistic concurrency via row_version
/// (AUT-008), keeps SpaceId immutable (AUT-002), and reports revision history + diff as impact
/// analysis (AUT-007).
/// </summary>
public sealed class AutomationService : IAutomationService
{
    private readonly IAutomationRepository _repository;
    private readonly IIdGenerator _idGenerator;
    private readonly IClock _clock;

    public AutomationService(IAutomationRepository repository, IIdGenerator idGenerator, IClock clock)
    {
        _repository = repository;
        _idGenerator = idGenerator;
        _clock = clock;
    }

    public async Task<Result<AutomationDefinition>> CreateAsync(CreateAutomationRequest r, CancellationToken ct)
    {
        var id = AutomationId.Create(_idGenerator);
        var revisionId = AutomationRevisionId.Create(_idGenerator);
        var now = _clock.UtcNow;
        var revision = AutomationRevision.Create(revisionId, id, 1, r.Trigger, r.Workflow, r.Binding,
            r.Budget, r.OverlapPolicy, r.MissedRunPolicy, r.Permission, now);
        var create = AutomationDefinition.Create(id, r.SpaceId, r.Name, r.Description, revisionId, now);
        if (!create.IsSuccess) return create.Error!;
        return await _repository.SaveAsync(create.Value!, revision, ct);
    }

    public Task<Result<AutomationDefinition>> GetAsync(AutomationId id, CancellationToken ct) => _repository.GetAsync(id, ct);

    public async Task<Result<AutomationRevision>> GetCurrentRevisionAsync(AutomationId id, CancellationToken ct)
    {
        var get = await _repository.GetAsync(id, ct);
        if (!get.IsSuccess) return get.Error!;
        return await _repository.GetRevisionAsync(get.Value!.CurrentRevisionId, ct);
    }

    public Task<Result<IReadOnlyList<AutomationDefinition>>> ListBySpaceAsync(SpaceId spaceId, bool includeDeleted, CancellationToken ct)
        => _repository.ListBySpaceAsync(spaceId, includeDeleted, ct);

    public async Task<Result<AutomationDefinition>> UpdateDraftAsync(UpdateAutomationRequest r, CancellationToken ct)
    {
        var get = await _repository.GetAsync(r.Id, ct);
        if (!get.IsSuccess) return get.Error!;
        var def = get.Value!;
        if (def.RowVersion != r.ExpectedRowVersion)
            return AutomationErrors.ConcurrencyConflictError();

        var rename = def.Rename(r.Name);
        if (!rename.IsSuccess) return rename.Error!;
        var desc = def.ChangeDescription(r.Description);
        if (!desc.IsSuccess) return desc.Error!;

        var contentChanged = r.Trigger is not null || r.Workflow is not null || r.Binding is not null ||
                             r.Budget is not null || r.OverlapPolicy is not null || r.MissedRunPolicy is not null || r.Permission is not null;
        if (contentChanged)
        {
            var current = await _repository.GetRevisionAsync(def.CurrentRevisionId, ct);
            if (!current.IsSuccess) return current.Error!;
            var cur = current.Value!;
            var trigger = r.Trigger ?? cur.Trigger;
            var workflow = r.Workflow ?? cur.Workflow;
            var binding = r.Binding ?? cur.Binding;
            var budget = r.Budget ?? cur.Budget;
            var overlap = r.OverlapPolicy ?? cur.OverlapPolicy;
            var missed = r.MissedRunPolicy ?? cur.MissedRunPolicy;
            var permission = r.Permission ?? cur.PermissionRequest;

            var newRevisionId = AutomationRevisionId.Create(_idGenerator);
            var newRevision = AutomationRevision.Create(newRevisionId, def.Id, def.RevisionNumber + 1,
                trigger, workflow, binding, budget, overlap, missed, permission, _clock.UtcNow);
            var promote = def.PromoteDraftRevision(newRevisionId, def.RevisionNumber + 1);
            if (!promote.IsSuccess) return promote.Error!;
            def.Touch(_clock.UtcNow);
            return await _repository.SaveAsync(def, newRevision, ct);
        }

        def.Touch(_clock.UtcNow);
        return await _repository.SaveAsync(def, null, ct);
    }

    public async Task<Result<AutomationDefinition>> PublishAsync(AutomationId id, AutomationRevisionId revisionId, long expectedRowVersion, CancellationToken ct)
    {
        var get = await _repository.GetAsync(id, ct);
        if (!get.IsSuccess) return get.Error!;
        var def = get.Value!;
        if (def.RowVersion != expectedRowVersion)
            return AutomationErrors.ConcurrencyConflictError();

        var rev = await _repository.GetRevisionAsync(revisionId, ct);
        if (!rev.IsSuccess) return rev.Error!;
        var publish = def.Publish(revisionId, rev.Value!.RevisionNumber);
        if (!publish.IsSuccess) return publish.Error!;
        def.Touch(_clock.UtcNow);
        return await _repository.SaveAsync(def, null, ct);
    }

    public async Task<Result<AutomationDefinition>> ArchiveAsync(AutomationId id, long expectedRowVersion, CancellationToken ct)
    {
        var get = await _repository.GetAsync(id, ct);
        if (!get.IsSuccess) return get.Error!;
        var def = get.Value!;
        if (def.RowVersion != expectedRowVersion)
            return AutomationErrors.ConcurrencyConflictError();
        var archive = def.Archive();
        if (!archive.IsSuccess) return archive.Error!;
        def.Touch(_clock.UtcNow);
        return await _repository.SaveAsync(def, null, ct);
    }

    public async Task<Result<AutomationDefinition>> SoftDeleteAsync(AutomationId id, long expectedRowVersion, CancellationToken ct)
    {
        var get = await _repository.GetAsync(id, ct);
        if (!get.IsSuccess) return get.Error!;
        var def = get.Value!;
        if (def.RowVersion != expectedRowVersion)
            return AutomationErrors.ConcurrencyConflictError();
        var delete = def.SoftDelete();
        if (!delete.IsSuccess) return delete.Error!;
        def.Touch(_clock.UtcNow);
        return await _repository.SaveAsync(def, null, ct);
    }

    public async Task<Result<AutomationDefinition>> CopyAsync(AutomationId sourceId, SpaceId? targetSpaceId, CancellationToken ct)
    {
        var get = await _repository.GetAsync(sourceId, ct);
        if (!get.IsSuccess) return get.Error!;
        var src = get.Value!;
        var targetSpace = targetSpaceId ?? src.SpaceId;
        var current = await _repository.GetRevisionAsync(src.CurrentRevisionId, ct);
        if (!current.IsSuccess) return current.Error!;
        var cur = current.Value!;

        var newId = AutomationId.Create(_idGenerator);
        var newRevisionId = AutomationRevisionId.Create(_idGenerator);
        var now = _clock.UtcNow;
        var revision = AutomationRevision.Create(newRevisionId, newId, 1, cur.Trigger, cur.Workflow,
            cur.Binding, cur.Budget, cur.OverlapPolicy, cur.MissedRunPolicy, cur.PermissionRequest, now);
        var create = AutomationDefinition.Create(newId, targetSpace, src.Name + " (copy)", src.Description, newRevisionId, now);
        if (!create.IsSuccess) return create.Error!;
        return await _repository.SaveAsync(create.Value!, revision, ct);
    }

    public async Task<Result<ImpactAnalysis>> AnalyzeImpactAsync(AutomationId id, CancellationToken ct)
    {
        var get = await _repository.GetAsync(id, ct);
        if (!get.IsSuccess) return get.Error!;
        var def = get.Value!;

        var revs = await _repository.GetRevisionsAsync(id, ct);
        if (!revs.IsSuccess) return revs.Error!;
        var list = revs.Value!;

        var diffs = new List<RevisionDiff>();
        for (var i = 1; i < list.Count; i++)
            diffs.Add(RevisionDiff.Compute(list[i - 1], list[i]));

        var notes = new List<string>();
        if (def.Lifecycle == AutomationLifecycle.Deleted)
            notes.Add("Automation is soft-deleted; identity is retained for historical run references.");
        if (!diffs.Any())
            notes.Add("No revision history diff available yet.");

        // Run-reference checking is unavailable until the run tables exist (T07).
        return Result<ImpactAnalysis>.Ok(new ImpactAnalysis(
            def.Id, list.Count, def.CurrentRevisionId,
            RunReferenceCheckAvailable: false, RunsReferencingCurrentRevision: 0,
            diffs, notes));
    }
}
