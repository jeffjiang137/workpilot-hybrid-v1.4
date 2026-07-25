using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;

namespace WorkPilot.Application.Security.Retention;

/// <summary>
/// Batch deletion engine for retention cleanup (doc 05 §9, SEC-106). Every delete is bounded by
/// <see cref="Limits.V1_5.RetentionCleanupBatchSize"/> and runs inside its own transaction so a single
/// batch cannot exceed the ~200 ms budget. The implementation decides which runs are "terminal"
/// (only <c>Completed/Failed/Cancelled</c> are eligible — non-terminal runs such as waiting-approval
/// or needs-review are never returned, satisfying "protected records are not deleted by time").
/// </summary>
public interface IRetentionStore
{
    /// <summary>Up to <paramref name="batchSize"/> terminal-run ids whose finished time is before <paramref name="runCutoff"/>.</summary>
    Task<Result<IReadOnlyList<RunId>>> GetDeletableRunIdsAsync(DateTimeOffset runCutoff, int batchSize, CancellationToken ct = default);

    /// <summary>Cascade-delete one run's events, steps, and run row in a single transaction. Returns rows deleted.</summary>
    Task<Result<int>> DeleteRunCascadeAsync(RunId id, CancellationToken ct = default);

    /// <summary>Delete run events older than <paramref name="eventCutoff"/> (these belong to still-retained runs). Returns rows deleted.</summary>
    Task<Result<int>> DeleteRunEventsOlderThanAsync(DateTimeOffset eventCutoff, int batchSize, CancellationToken ct = default);

    /// <summary>Delete security audit records older than <paramref name="auditCutoff"/>. Returns rows deleted.</summary>
    Task<Result<int>> DeleteAuditRecordsOlderThanAsync(DateTimeOffset auditCutoff, int batchSize, CancellationToken ct = default);

    /// <summary>Delete only <b>resolved</b> incidents older than <paramref name="auditCutoff"/> (open incidents are protected). Returns rows deleted.</summary>
    Task<Result<int>> DeleteResolvedIncidentsOlderThanAsync(DateTimeOffset auditCutoff, int batchSize, CancellationToken ct = default);
}
