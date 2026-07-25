using WorkPilot.Contracts.Primitives;

namespace WorkPilot.Application.Automation.Run.Permit;

/// <summary>
/// Owns a single-use permit for the duration of one adapter invocation. The adapter receives this and
/// must consume it before the first I/O. Disposing the lease without consumption revokes the permit, so
/// a crashed or abandoned invocation cannot later be replayed (doc 04 §9, PER-006).
/// </summary>
public sealed class ExecutionPermitLease : System.IDisposable
{
    private readonly IExecutionPermit _permit;
    private PermitLiveState? _live;
    private bool _disposed;

    public ExecutionPermitLease(IExecutionPermit permit)
    {
        _permit = permit ?? throw new System.ArgumentNullException(nameof(permit));
    }

    /// <summary>The bound permit handle. The adapter consumes this before any I/O.</summary>
    public IExecutionPermit Permit => _permit;

    /// <summary>Sets the live per-run state used for the send-time current-state check.</summary>
    public void SetLiveState(PermitLiveState live) => _live = live;

    /// <summary>Adapter-facing single-use consume. Reads the live state set by the executor.</summary>
    public System.Threading.Tasks.Task<Result<PermitConsumption>> ConsumeAndCheckAsync(System.Threading.CancellationToken ct = default)
        => _permit.ConsumeAndCheckAsync(_live ?? new PermitLiveState("", System.DateTimeOffset.MaxValue, false), ct);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_permit.IsConsumed)
            _permit.Revoke();
    }
}
