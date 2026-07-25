using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Application.Automation.Run;
using WorkPilot.Application.Automation.Run.Approval;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation.Run;

namespace WorkPilot.App.Core.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IRunRepository"/> for App.Core view-model tests. Faithfully implements the
/// stable keyset pagination contract (LOG-001): order by (StartedAtUtc DESC, Id DESC); runs without a
/// started time sort last; cursor is the last row's (StartedAtUtc, Id). Unused write paths are no-ops.
/// </summary>
public sealed class StubRunRepository : IRunRepository
{
    private readonly Dictionary<RunId, RunWithDetails> _store = new();

    /// <summary>Injected failure for <see cref="ListRunsAsync"/> (null = succeed).</summary>
    public AppError? ListError { get; set; }
    /// <summary>Injected failure for <see cref="GetRunAsync"/> (null = succeed).</summary>
    public AppError? GetError { get; set; }

    public void Seed(RunWithDetails d) => _store[d.Run.Id] = d;

    public IReadOnlyCollection<RunWithDetails> All => _store.Values.ToList();

    public Task<Result> CreateRunAsync(AutomationRun run, RunSnapshot snapshot, TriggerOccurrence? occurrence, CancellationToken ct)
    {
        _store[run.Id] = new RunWithDetails(run, snapshot, Array.Empty<StepRun>(), Array.Empty<RunEvent>());
        return Task.FromResult(Result.Success());
    }

    public Task<Result<RunWithDetails?>> GetRunAsync(RunId id, CancellationToken ct)
    {
        if (GetError is not null) return Task.FromResult(Result<RunWithDetails?>.Fail(GetError!));
        return Task.FromResult(Result<RunWithDetails?>.Ok(_store.TryGetValue(id, out var d) ? d : null));
    }

    public Task<Result> AppendEventAsync(RunEvent ev, CancellationToken ct) => Task.FromResult(Result.Success());
    public Task<Result> AppendEventsAsync(IReadOnlyList<RunEvent> events, CancellationToken ct) => Task.FromResult(Result.Success());

    public Task<Result<RunListPage>> ListRunsAsync(RunQuery query, CancellationToken ct)
    {
        if (ListError is not null) return Task.FromResult(Result<RunListPage>.Fail(ListError!));

        var items = _store.Values
            .Select(d => ToItem(d.Run))
            .Where(i => Matches(i, query))
            .OrderBy(i => i.StartedAtUtc.HasValue ? 0 : 1)          // started runs before nulls (history view)
            .ThenByDescending(i => i.StartedAtUtc ?? DateTimeOffset.MinValue)
            .ThenByDescending(i => i.Id.Value)
            .ToList();

        int start = 0;
        if (query.Cursor is not null)
        {
            var idx = items.FindIndex(i => i.StartedAtUtc == query.Cursor.StartedAtUtc && i.Id == query.Cursor.Id);
            start = idx < 0 ? 0 : idx + 1;
        }

        var page = items.Skip(start).Take(query.PageSize).ToList();
        var hasMore = start + query.PageSize < items.Count;
        var next = hasMore ? new RunListCursor(page[^1].StartedAtUtc, page[^1].Id) : null;
        return Task.FromResult(Result<RunListPage>.Ok(new RunListPage(page, hasMore, next)));
    }

    public Task<Result<bool>> TryClaimAsync(RunId id, string owner, DateTimeOffset leaseExpiresAt, CancellationToken ct)
        => Task.FromResult(Result<bool>.Ok(true));

    public Task<Result> RequestCancellationAsync(RunId id, DateTimeOffset now, CancellationToken ct)
    {
        if (_store.TryGetValue(id, out var d))
            _store[id] = d with { Run = d.Run.RequestCancellation(now) };
        return Task.FromResult(Result.Success());
    }

    public Task<Result> CancelAsync(RunId id, DateTimeOffset now, CancellationToken ct) => Task.FromResult(Result.Success());
    public Task<Result> DeleteRunAsync(RunId id, CancellationToken ct) => Task.FromResult(Result.Success());
    public Task<Result> UpsertStepAsync(StepRun step, CancellationToken ct) => Task.FromResult(Result.Success());
    public Task<Result> PersistExecutionResultAsync(AutomationRun run, IReadOnlyList<StepRun> steps, IReadOnlyList<RunEvent> events, CancellationToken ct)
        => Task.FromResult(Result.Success());

    private static bool Matches(RunListItem i, RunQuery q)
    {
        if (q.AutomationId is not null && i.AutomationId != q.AutomationId) return false;
        if (q.Status is not null && i.Status != q.Status) return false;
        if (q.TriggerKind is not null && i.TriggerKind != q.TriggerKind) return false;
        if (q.FromUtc is not null && (i.StartedAtUtc is null || i.StartedAtUtc < q.FromUtc)) return false;
        if (q.ToUtc is not null && (i.StartedAtUtc is null || i.StartedAtUtc > q.ToUtc)) return false;
        return true;
    }

    private static RunListItem ToItem(AutomationRun r) =>
        new(r.Id, r.AutomationId, r.AutomationRevisionId, r.TriggerKind, r.Status, r.Priority,
            r.ScheduledAtUtc, r.StartedAtUtc, r.FinishedAtUtc, r.FinalErrorCode);
}
