using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Contracts.Primitives;

namespace WorkPilot.Application.Automation.Materialization;

/// <summary>
/// Persistence port for the domain-event outbox (spec doc 04 §4). Business transactions append safe
/// event projections; the dispatcher reads pending rows, matches them to domain-event triggers, and
/// marks them dispatched (or failed with bounded retries). Implemented by Infrastructure against
/// <c>domain_event_outbox</c>.
/// </summary>
public interface IDomainEventOutboxStore
{
    /// <summary>Returns up to <paramref name="batchSize"/> pending (undispatched) outbox events, oldest first.</summary>
    Task<Result<IReadOnlyList<PendingOutboxEvent>>> GetPendingAsync(int batchSize, CancellationToken ct);

    /// <summary>Marks an outbox event dispatched (idempotent).</summary>
    Task<Result> MarkDispatchedAsync(string outboxId, DateTimeOffset now, CancellationToken ct);

    /// <summary>Records a failed dispatch attempt; after <paramref name="maxAttempts"/> it is left for incident generation (T19).</summary>
    Task<Result> MarkFailedAsync(string outboxId, DateTimeOffset now, DateTimeOffset nextAttemptAt, string? errorCode, int maxAttempts, CancellationToken ct);
}
