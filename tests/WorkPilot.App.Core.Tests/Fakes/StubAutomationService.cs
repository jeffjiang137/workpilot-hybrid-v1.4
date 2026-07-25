using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Application.Automation;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation;

namespace WorkPilot.App.Core.Tests.Fakes;

/// <summary>
/// Faithful in-memory replica of <see cref="AutomationService"/> semantics for editor tests: same
/// optimistic-concurrency contract (AUT-008), same Draft-only update rule, same Publish transition.
/// No SQLite, no I/O — just enough to exercise the editor's save/conflict orchestration.
/// </summary>
public sealed class StubAutomationService : IAutomationService
{
    private readonly IIdGenerator _ids;
    private readonly IClock _clock;
    private readonly Dictionary<string, AutomationDefinition> _defs = new();
    private readonly Dictionary<string, AutomationRevision> _revs = new();

    public StubAutomationService(IIdGenerator ids, IClock clock)
    {
        _ids = ids;
        _clock = clock;
    }

    public Task<Result<AutomationDefinition>> CreateAsync(CreateAutomationRequest r, CancellationToken ct = default)
    {
        var id = AutomationId.Create(_ids);
        var revId = AutomationRevisionId.Create(_ids);
        var now = _clock.UtcNow;
        var rev = AutomationRevision.Create(revId, id, 1, r.Trigger, r.Workflow, r.Binding, r.Budget,
            r.OverlapPolicy, r.MissedRunPolicy, r.Permission, now);
        var def = AutomationDefinition.Create(id, r.SpaceId, r.Name, r.Description, revId, now).Value!;
        def.RowVersion = 1;
        _defs[id.Value] = def;
        _revs[revId.Value] = rev;
        return Task.FromResult(Result<AutomationDefinition>.Ok(def));
    }

    public Task<Result<AutomationDefinition>> GetAsync(AutomationId id, CancellationToken ct = default)
    {
        if (_defs.TryGetValue(id.Value, out var def))
            return Task.FromResult(Result<AutomationDefinition>.Ok(def));
        return Task.FromResult(Result<AutomationDefinition>.Fail(AutomationErrors.NotFoundError()));
    }

    public Task<Result<IReadOnlyList<AutomationDefinition>>> ListBySpaceAsync(SpaceId spaceId, bool includeDeleted = false, CancellationToken ct = default)
    {
        var list = new List<AutomationDefinition>();
        foreach (var d in _defs.Values)
            if (d.SpaceId == spaceId && (includeDeleted || d.Lifecycle != AutomationLifecycle.Deleted))
                list.Add(d);
        return Task.FromResult(Result<IReadOnlyList<AutomationDefinition>>.Ok(list));
    }

    public Task<Result<AutomationDefinition>> UpdateDraftAsync(UpdateAutomationRequest r, CancellationToken ct = default)
    {
        if (!_defs.TryGetValue(r.Id.Value, out var def))
            return Task.FromResult(Result<AutomationDefinition>.Fail(AutomationErrors.NotFoundError()));
        if (def.RowVersion != r.ExpectedRowVersion)
            return Task.FromResult(Result<AutomationDefinition>.Fail(AutomationErrors.ConcurrencyConflictError()));
        if (def.Lifecycle != AutomationLifecycle.Draft)
            return Task.FromResult(Result<AutomationDefinition>.Fail(
                AutomationErrors.InvalidTransitionError(def.Lifecycle, AutomationLifecycle.Draft)));

        var rename = def.Rename(r.Name);
        if (!rename.IsSuccess) return Task.FromResult(Result<AutomationDefinition>.Fail(rename.Error!));
        var desc = def.ChangeDescription(r.Description);
        if (!desc.IsSuccess) return Task.FromResult(Result<AutomationDefinition>.Fail(desc.Error!));

        var cur = _revs[def.CurrentRevisionId.Value];
        var trigger = r.Trigger ?? cur.Trigger;
        var workflow = r.Workflow ?? cur.Workflow;
        var binding = r.Binding ?? cur.Binding;
        var budget = r.Budget ?? cur.Budget;
        var overlap = r.OverlapPolicy ?? cur.OverlapPolicy;
        var missed = r.MissedRunPolicy ?? cur.MissedRunPolicy;
        var permission = r.Permission ?? cur.PermissionRequest;

        var revId = AutomationRevisionId.Create(_ids);
        var rev = AutomationRevision.Create(revId, def.Id, def.RevisionNumber + 1, trigger, workflow,
            binding, budget, overlap, missed, permission, _clock.UtcNow);
        def.PromoteDraftRevision(revId, def.RevisionNumber + 1);
        def.Touch(_clock.UtcNow);
        def.RowVersion++;
        _revs[revId.Value] = rev;
        return Task.FromResult(Result<AutomationDefinition>.Ok(def));
    }

    public Task<Result<AutomationDefinition>> PublishAsync(AutomationId id, AutomationRevisionId revisionId, long expectedRowVersion, CancellationToken ct = default)
    {
        if (!_defs.TryGetValue(id.Value, out var def))
            return Task.FromResult(Result<AutomationDefinition>.Fail(AutomationErrors.NotFoundError()));
        if (def.RowVersion != expectedRowVersion)
            return Task.FromResult(Result<AutomationDefinition>.Fail(AutomationErrors.ConcurrencyConflictError()));
        var publish = def.Publish(revisionId, def.RevisionNumber);
        if (!publish.IsSuccess) return Task.FromResult(Result<AutomationDefinition>.Fail(publish.Error!));
        def.RowVersion++;
        return Task.FromResult(Result<AutomationDefinition>.Ok(def));
    }

    public Task<Result<AutomationDefinition>> ArchiveAsync(AutomationId id, long expectedRowVersion, CancellationToken ct = default)
    {
        if (!_defs.TryGetValue(id.Value, out var def)) return Task.FromResult(Result<AutomationDefinition>.Fail(AutomationErrors.NotFoundError()));
        if (def.RowVersion != expectedRowVersion) return Task.FromResult(Result<AutomationDefinition>.Fail(AutomationErrors.ConcurrencyConflictError()));
        def.Archive();
        def.RowVersion++;
        return Task.FromResult(Result<AutomationDefinition>.Ok(def));
    }

    public Task<Result<AutomationDefinition>> SoftDeleteAsync(AutomationId id, long expectedRowVersion, CancellationToken ct = default)
    {
        if (!_defs.TryGetValue(id.Value, out var def)) return Task.FromResult(Result<AutomationDefinition>.Fail(AutomationErrors.NotFoundError()));
        if (def.RowVersion != expectedRowVersion) return Task.FromResult(Result<AutomationDefinition>.Fail(AutomationErrors.ConcurrencyConflictError()));
        def.SoftDelete();
        def.RowVersion++;
        return Task.FromResult(Result<AutomationDefinition>.Ok(def));
    }

    public Task<Result<AutomationDefinition>> CopyAsync(AutomationId sourceId, SpaceId? targetSpaceId, CancellationToken ct = default)
    {
        if (!_defs.TryGetValue(sourceId.Value, out var src)) return Task.FromResult(Result<AutomationDefinition>.Fail(AutomationErrors.NotFoundError()));
        var cur = _revs[src.CurrentRevisionId.Value];
        var newId = AutomationId.Create(_ids);
        var revId = AutomationRevisionId.Create(_ids);
        var now = _clock.UtcNow;
        var rev = AutomationRevision.Create(revId, newId, 1, cur.Trigger, cur.Workflow, cur.Binding,
            cur.Budget, cur.OverlapPolicy, cur.MissedRunPolicy, cur.PermissionRequest, now);
        var create = AutomationDefinition.Create(newId, targetSpaceId ?? src.SpaceId, src.Name + " (copy)", src.Description, revId, now).Value!;
        create.RowVersion = 1;
        _defs[newId.Value] = create;
        _revs[revId.Value] = rev;
        return Task.FromResult(Result<AutomationDefinition>.Ok(create));
    }

    public Task<Result<ImpactAnalysis>> AnalyzeImpactAsync(AutomationId id, CancellationToken ct = default)
    {
        if (!_defs.TryGetValue(id.Value, out var def)) return Task.FromResult(Result<ImpactAnalysis>.Fail(AutomationErrors.NotFoundError()));
        return Task.FromResult(Result<ImpactAnalysis>.Ok(new ImpactAnalysis(def.Id, 1, def.CurrentRevisionId,
            false, 0, System.Array.Empty<RevisionDiff>(), System.Array.Empty<string>())));
    }

    public Task<Result<AutomationRevision>> GetCurrentRevisionAsync(AutomationId id, CancellationToken ct = default)
    {
        if (!_defs.TryGetValue(id.Value, out var def)) return Task.FromResult(Result<AutomationRevision>.Fail(AutomationErrors.NotFoundError()));
        if (_revs.TryGetValue(def.CurrentRevisionId.Value, out var rev))
            return Task.FromResult(Result<AutomationRevision>.Ok(rev));
        return Task.FromResult(Result<AutomationRevision>.Fail(AutomationErrors.RevisionNotFoundError()));
    }

    /// <summary>Simulates another user saving, bumping the stored row version so the next save conflicts.</summary>
    public void SimulateExternalEdit(AutomationId id) => _defs[id.Value].RowVersion++;

    public long StoredRowVersion(AutomationId id) => _defs[id.Value].RowVersion;
}
