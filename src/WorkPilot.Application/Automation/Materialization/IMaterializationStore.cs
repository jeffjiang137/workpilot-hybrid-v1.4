using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation.Run;
using WorkPilot.Domain.Automation.Run.Materialization;
using WorkPilot.Domain.Automation.Scheduling;

namespace WorkPilot.Application.Automation.Materialization;

/// <summary>
/// Persistence port for run/occurrence/snapshot materialization and claim/lease bookkeeping
/// (spec doc 04 §3–§6, RUN-001/002/009/010). Implemented by Infrastructure against SQLite. Every
/// method returns <see cref="Result"/> so the Application layer never throws on expected storage
/// failures; claim/lease operations are atomic under a write lock to guarantee "at most one claim".
/// </summary>
public interface IMaterializationStore
{
    /// <summary>
    /// Idempotently reserves a trigger occurrence row (the dedupe key is UNIQUE). Returns
    /// <c>true</c> when inserted, <c>false</c> when a duplicate dedupe key already existed — the
    /// caller then treats the candidate as already materialized and advances its pointer.
    /// </summary>
    Task<Result<bool>> TryReserveOccurrenceAsync(TriggerOccurrence occurrence, CancellationToken ct);

    /// <summary>Atomically persists a frozen snapshot + queued run bound to a previously reserved occurrence, plus the run_created event.</summary>
    Task<Result> CreateRunForOccurrenceAsync(AutomationRun run, RunSnapshot snapshot, RunEvent createdEvent, CancellationToken ct);

    /// <summary>Appends a batch of run events (single writer) with contiguous per-run sequences — used to record claim/lease/recovery observability after the authoritative state change.</summary>
    Task<Result> AppendEventsAsync(IReadOnlyList<RunEvent> events, CancellationToken ct);

    /// <summary>Coalesces a new candidate into an existing queued run (queue_one policy): bumps coalesced_count and appends a coalesced event.</summary>
    Task<Result> RecordCoalesceAsync(RunId targetRunId, int coalescedCount, TriggerOccurrence occurrence, RunEvent coalescedEvent, CancellationToken ct);

    /// <summary>Active (non-terminal) runs for an automation, needed to evaluate overlap policy.</summary>
    Task<Result<IReadOnlyList<ExistingRunSummary>>> GetActiveRunsAsync(AutomationId automationId, CancellationToken ct);

    /// <summary>Queued runs eligible for claiming now (available_at_utc <= now, no cancellation requested), ordered for the claim selector.</summary>
    Task<Result<IReadOnlyList<QueuedRunInfo>>> GetClaimableQueuedAsync(DateTimeOffset now, int batchSize, CancellationToken ct);

    /// <summary>Requests cancellation on a run (cancel_previous overlap policy / cooperative cancel).</summary>
    Task<Result> RequestCancellationAsync(RunId runId, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// Atomically claims the given queued runs for <paramref name="owner"/>, enforcing per-automation
    /// concurrency (only one active execution) via a NOT EXISTS guard. Returns the run ids actually
    /// flipped to <c>claimed</c>; ids already claimed/running/terminal are silently excluded.
    /// </summary>
    Task<Result<IReadOnlyList<RunId>>> ClaimBatchAsync(IReadOnlyList<RunId> ids, string owner, DateTimeOffset leaseExpiresAt, DateTimeOffset now, CancellationToken ct);

    /// <summary>Heartbeat: extends the lease of owned, still-claimed runs (CAS on owner/status).</summary>
    Task<Result> HeartbeatAsync(string owner, DateTimeOffset leaseExpiresAt, IReadOnlyList<RunId> ids, CancellationToken ct);

    /// <summary>Releases a lease without changing status (graceful shutdown of an owned run).</summary>
    Task<Result> ReleaseLeaseAsync(RunId runId, CancellationToken ct);

    /// <summary>Returns runs whose lease expired and were still claimed/running (recovery candidates).</summary>
    Task<Result<IReadOnlyList<ExpiredLease>>> ScanExpiredLeasesAsync(DateTimeOffset now, int batchSize, CancellationToken ct);

    /// <summary>Applies recovery to one expired lease: requeue when no side effect is in flight, otherwise mark needs_review; fails repeatedly after the recovery cap.</summary>
    Task<Result> RecoverLeaseAsync(RunId runId, DateTimeOffset now, bool sideEffectInFlight, CancellationToken ct);
}
