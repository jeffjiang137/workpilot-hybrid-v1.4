using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation.Run;
using WorkPilot.Domain.Automation.Run.Materialization;

namespace WorkPilot.Application.Automation.Materialization;

/// <summary>
/// Worker-side claim and lease orchestration (spec doc 04 §6, RUN-002). A worker claims a bounded
/// batch of queued runs each tick (the pure <see cref="RunQueueSelector"/> decides the plan; the
/// store's atomic CAS is the authoritative guard against double-claim across workers), holds a
/// renewable 30s lease via heartbeats, and recovers runs whose lease expired (Host crash / lost
/// worker). Observability events (lease_acquired / recovered) are appended after the authoritative
/// state change so a crash between them never double-claims.
/// </summary>
public sealed class RunClaimService
{
    private readonly IMaterializationStore _store;
    private readonly IIdGenerator _ids;
    private readonly IClock _clock;
    private readonly string _workerId;
    private readonly TimeSpan _leaseDuration;
    private readonly int _globalSlots;

    public RunClaimService(
        IMaterializationStore store,
        IIdGenerator ids,
        IClock clock,
        string workerId,
        TimeSpan leaseDuration,
        int globalSlots)
    {
        _store = store;
        _ids = ids;
        _clock = clock;
        _workerId = workerId;
        _leaseDuration = leaseDuration;
        _globalSlots = Math.Max(1, Math.Min(globalSlots, RunQueueSelector.MaxGlobalSlots));
    }

    public RunClaimService(
        IMaterializationStore store,
        IIdGenerator ids,
        IClock clock,
        string workerId)
        : this(store, ids, clock, workerId, TimeSpan.FromSeconds(30), RunQueueSelector.DefaultGlobalSlots) { }

    /// <summary>Claims up to the configured global slots of eligible queued runs and returns what was actually claimed.</summary>
    public async Task<ClaimBatchResult> ClaimAvailableAsync(CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        var queued = (await _store.GetClaimableQueuedAsync(now, 500, ct)).ValueOrDefault(Array.Empty<QueuedRunInfo>());
        var plan = RunQueueSelector.Select(queued, _globalSlots);

        if (plan.Count == 0)
            return new ClaimBatchResult(0, Array.Empty<RunId>(), 0);

        var leaseExpires = now + _leaseDuration;
        var claimed = (await _store.ClaimBatchAsync(plan, _workerId, leaseExpires, now, ct)).ValueOrDefault(Array.Empty<RunId>());

        if (claimed.Count > 0)
        {
            var events = claimed.Select(id => RunEvent.Create(
                RunEventId.Create(_ids), id, EventKinds.LeaseAcquired, RunEventLevel.Info,
                "CLAIMED", MessageKeys.LeaseAcquired, "{}", _ids.NewId(), now)).ToList();
            await _store.AppendEventsAsync(events, ct);
        }

        return new ClaimBatchResult(claimed.Count, claimed, 0);
    }

    /// <summary>Renews the lease of runs this worker currently owns (called on the heartbeat cadence).</summary>
    public async Task<int> HeartbeatAsync(IReadOnlyList<RunId> ownedRunIds, CancellationToken ct = default)
    {
        if (ownedRunIds.Count == 0) return 0;
        var now = _clock.UtcNow;
        var leaseExpires = now + _leaseDuration;
        await _store.HeartbeatAsync(_workerId, leaseExpires, ownedRunIds, ct);
        return ownedRunIds.Count;
    }

    /// <summary>Recovers runs whose lease expired (requeue or mark needs_review), returning the recovered run ids.</summary>
    public async Task<IReadOnlyList<RunId>> RecoverExpiredAsync(CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        var expired = (await _store.ScanExpiredLeasesAsync(now, 50, ct)).ValueOrDefault(Array.Empty<ExpiredLease>());
        var recovered = new List<RunId>();
        foreach (var e in expired)
        {
            await _store.RecoverLeaseAsync(e.RunId, now, e.SideEffectInFlight, ct);
            recovered.Add(e.RunId);
        }
        return recovered;
    }
}
