using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Application.Automation.Run;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation.Run;

namespace WorkPilot.App.Core.Runs;

/// <summary>
/// Creates a fresh run that re-executes a previous run from the start (RUN-006 / RUN-A15). The new run
/// references the original via <see cref="AutomationRun.ParentRunId"/>; the original run is never
/// modified, so its history stays immutable and auditable. The frozen snapshot is cloned with a new id
/// but the same canonical hash (identical content).
/// </summary>
public sealed class RerunOrchestrator
{
    private readonly IRunRepository _runs;
    private readonly IIdGenerator _ids;
    private readonly IClock _clock;

    public RerunOrchestrator(IRunRepository runs, IIdGenerator ids, IClock clock)
    {
        _runs = runs ?? throw new System.ArgumentNullException(nameof(runs));
        _ids = ids ?? throw new System.ArgumentNullException(nameof(ids));
        _clock = clock ?? throw new System.ArgumentNullException(nameof(clock));
    }

    /// <summary>Creates a new run re-executing <paramref name="parentRunId"/>. Returns the new run id.</summary>
    public async Task<Result<RunId>> RerunAsync(RunId parentRunId, CancellationToken ct = default)
    {
        var get = await _runs.GetRunAsync(parentRunId, ct);
        if (!get.IsSuccess || get.Value is null)
            return Result<RunId>.Fail(get.Error ?? RunErrors.NotFoundError());
        var parent = get.Value;

        var newRunId = RunId.Create(_ids);
        var newSnapshotId = RunSnapshotId.Create(_ids);
        // Clone the frozen snapshot with a new id; canonical hash (content) is unchanged.
        var snapshot = parent.Snapshot with { Id = newSnapshotId };

        var now = _clock.UtcNow;
        var newRun = AutomationRun.Create(
            newRunId,
            parent.Run.AutomationRevisionId,
            newSnapshotId,
            parent.Run.TriggerKind,
            now,
            now,
            parent.Run.AutomationId,
            parent.Run.OccurrenceId,
            parentRunId, // ParentRunId points at the original; history is immutable.
            parent.Run.Priority);

        var created = await _runs.CreateRunAsync(newRun, snapshot, null, ct);
        if (!created.IsSuccess)
            return Result<RunId>.Fail(created.Error!);

        return Result<RunId>.Ok(newRunId);
    }
}
