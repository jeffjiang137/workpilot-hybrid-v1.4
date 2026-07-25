using System.Threading;
using System.Threading.Tasks;

namespace WorkPilot.Host.Core.Materialization;

/// <summary>One tick of the background materialization/claim loop (spec doc 04 §6/§7, RUN-001).</summary>
public sealed record EngineTickResult(
    int SchedulesProcessed,
    int RunsCreated,
    int Dispatched,
    int Claimed,
    int Recovered);

/// <summary>
/// The long-running host body (T09): on each heartbeat tick it materializes due scheduled triggers
/// and pending domain events, claims a bounded batch of queued runs under a renewable lease, recovers
/// runs whose lease expired (Host crash), and heartbeats the runs it currently owns. This is the
/// composition seam <see cref="WorkPilot.Host.Hosting.IHostWorker"/> plugs into; the engine itself is
/// BCL (net8.0) so it is fully unit-testable without Windows.
/// </summary>
public interface IMaterializationEngine
{
    Task<EngineTickResult> TickAsync(CancellationToken cancellationToken = default);
}
