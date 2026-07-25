using System;

namespace WorkPilot.Application.Automation.Run.Permit;

/// <summary>
/// Live, per-run state read at adapter send time for the second current-state check (doc 07 §11). The
/// executor fills this from the run just before the adapter's first I/O. A mismatch versus the permit's
/// sealed binding (lease owner/expiry, cancellation) makes the consume fail, so no I/O happens.
/// </summary>
public sealed record PermitLiveState(
    string WorkerLeaseOwner,
    DateTimeOffset LeaseExpiresAtUtc,
    bool CancellationRequested);
