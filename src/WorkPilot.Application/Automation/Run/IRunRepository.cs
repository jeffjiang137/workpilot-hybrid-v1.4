using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation.Run;

namespace WorkPilot.Application.Automation.Run;

/// <summary>
/// Persistence port for durable runs (RUN-002/003, LOG-001/002). Implemented by the Infrastructure
/// layer against SQLite. All operations return <see cref="Result"/> so the Application layer never
/// throws on expected storage failures.
/// </summary>
public interface IRunRepository
{
    /// <summary>
    /// Atomically persists a new run together with its frozen snapshot (and optional trigger
    /// occurrence) in a single transaction. The run is created in <c>Queued</c> state.
    /// </summary>
    Task<Result> CreateRunAsync(AutomationRun run, RunSnapshot snapshot, TriggerOccurrence? occurrence, CancellationToken ct);

    /// <summary>Loads a run with its snapshot, steps and events. Returns <c>null</c> if not found.</summary>
    Task<Result<RunWithDetails?>> GetRunAsync(RunId id, CancellationToken ct);

    /// <summary>Appends a single run event, assigning a monotonic, unique-per-run sequence under concurrency.</summary>
    Task<Result> AppendEventAsync(RunEvent ev, CancellationToken ct);

    /// <summary>Appends a batch of run events (single writer) with contiguous sequences.</summary>
    Task<Result> AppendEventsAsync(IReadOnlyList<RunEvent> events, CancellationToken ct);

    /// <summary>
    /// Lists runs with stable keyset pagination (LOG-001). Ordered by (started_at_utc DESC, id DESC);
    /// the cursor is the last row's (started_at_utc, id). Runs without a started time sort last and
    /// are not reached by pagination (history view), matching the provided <c>ix_runs_history</c> index.
    /// </summary>
    Task<Result<RunListPage>> ListRunsAsync(RunQuery query, CancellationToken ct);

    /// <summary>
    /// Claims a queued run for <paramref name="owner"/> with a lease that expires at
    /// <paramref name="leaseExpiresAt"/>. Atomic CAS on <c>status='queued'</c>; returns <c>false</c>
    /// when the run is no longer claimable (already claimed, running, terminal) — no exception.
    /// </summary>
    Task<Result<bool>> TryClaimAsync(RunId id, string owner, DateTimeOffset leaseExpiresAt, CancellationToken ct);

    /// <summary>Records a cancellation request on a run (does not immediately terminate it).</summary>
    Task<Result> RequestCancellationAsync(RunId id, DateTimeOffset now, CancellationToken ct);

    /// <summary>Cancels a run (terminal).</summary>
    Task<Result> CancelAsync(RunId id, DateTimeOffset now, CancellationToken ct);

    /// <summary>Deletes a run; steps and events cascade via ON DELETE CASCADE (FK test).</summary>
    Task<Result> DeleteRunAsync(RunId id, CancellationToken ct);

    /// <summary>
    /// Inserts or updates a single step run (T10 interpreter output). Idempotent by primary key
    /// <c>id</c>; the step's <c>row_version</c> reflects the optimistic-concurrency generation.
    /// </summary>
    Task<Result> UpsertStepAsync(StepRun step, CancellationToken ct);

    /// <summary>
    /// Atomically persists a single interpreter pass (T10, RUN-002/004): updates the run header
    /// (status, counters, current node, resume-at, row_version), upserts every affected step, and
    /// appends the emitted events with contiguous per-run sequences — all in one transaction so a
    /// crash never leaves a partially applied pass. Returns <see cref="Result"/>; storage failures
    /// roll back and never throw.
    /// </summary>
    Task<Result> PersistExecutionResultAsync(
        AutomationRun run,
        IReadOnlyList<StepRun> steps,
        IReadOnlyList<RunEvent> events,
        CancellationToken ct);
}
