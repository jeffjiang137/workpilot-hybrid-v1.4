using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Application.Automation.Materialization;
using WorkPilot.Contracts.Primitives.Ids;

namespace WorkPilot.Host.Core.Materialization;

/// <summary>
/// Default <see cref="IMaterializationEngine"/>. It wires the Application-layer materializer,
/// outbox dispatcher and claim service into the host tick (spec doc 04 §6/§7). The engine keeps the
/// set of run ids it currently owns in memory and renews their leases each tick; a lost lease (worker
/// crash, Host restart) is detected by <see cref="RunClaimService.RecoverExpiredAsync"/> and the run
/// is safely requeued — satisfying T09's DoD that "Host crash 可恢复" with no double execution.
/// </summary>
public sealed class MaterializationEngine : IMaterializationEngine
{
    private readonly TriggerMaterializer _materializer;
    private readonly DomainEventDispatcher _dispatcher;
    private readonly RunClaimService _claims;
    private readonly object _ownedLock = new();
    private readonly List<RunId> _owned = new();

    public MaterializationEngine(
        TriggerMaterializer materializer,
        DomainEventDispatcher dispatcher,
        RunClaimService claims)
    {
        _materializer = materializer;
        _dispatcher = dispatcher;
        _claims = claims;
    }

    /// <summary>Run ids this engine currently holds a lease on (for diagnostics / tests).</summary>
    public IReadOnlyList<RunId> OwnedRunIds
    {
        get { lock (_ownedLock) return _owned.ToList(); }
    }

    public async Task<EngineTickResult> TickAsync(CancellationToken cancellationToken = default)
    {
        var materialized = await _materializer.MaterializeDueAsync(cancellationToken);
        var dispatched = await _dispatcher.DispatchPendingAsync(cancellationToken);

        var claim = await _claims.ClaimAvailableAsync(cancellationToken);
        if (claim.ClaimedRunIds.Count > 0)
        {
            lock (_ownedLock)
                _owned.AddRange(claim.ClaimedRunIds);
        }

        var recovered = await _claims.RecoverExpiredAsync(cancellationToken);
        if (recovered.Count > 0)
        {
            lock (_ownedLock)
                _owned.RemoveAll(id => recovered.Contains(id));
        }

        IReadOnlyList<RunId> snapshot;
        lock (_ownedLock)
            snapshot = _owned.ToList();
        if (snapshot.Count > 0)
            await _claims.HeartbeatAsync(snapshot, cancellationToken);

        return new EngineTickResult(
            materialized.SchedulesProcessed,
            materialized.RunsCreated,
            dispatched,
            claim.Claimed,
            recovered.Count);
    }
}
