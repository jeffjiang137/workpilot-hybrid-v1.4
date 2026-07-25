using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Contracts.Primitives;

namespace WorkPilot.Application.Automation.Run.Permit;

/// <summary>
/// Opaque single-use permit handle. Only the Native Core can construct one (C# callers can never forge
/// it). Consuming is the send-time current-state check that mirrors native
/// <c>wp_permit_consume_and_check</c>: on any failure the permit is left unconsumed and the caller must
/// NOT open a socket / write a pipe.
/// </summary>
public interface IExecutionPermit
{
    /// <summary>True once <see cref="ConsumeAndCheckAsync"/> has succeeded. A consumed permit can never
    /// be consumed again and cannot be revoked.</summary>
    bool IsConsumed { get; }

    /// <summary>Atomically consume (single-use) and verify the permit against live state. Returns
    /// <see cref="Result.IsSuccess"/> false (never throws) when the permit is invalid, expired,
    /// already consumed, or the live state no longer authorizes the send.</summary>
    Task<Result<PermitConsumption>> ConsumeAndCheckAsync(PermitLiveState live, CancellationToken ct = default);

    /// <summary>Revoke an un-consumed permit (e.g. a lease disposed before send). No-op if consumed.</summary>
    void Revoke();
}

/// <summary>Proof returned by a successful <see cref="IExecutionPermit.ConsumeAndCheckAsync"/>.</summary>
public sealed class PermitConsumption
{
    public PermitBinding Binding { get; }
    public DateTimeOffset ConsumedAtUtc { get; }

    public PermitConsumption(PermitBinding binding, DateTimeOffset consumedAtUtc)
    {
        Binding = binding;
        ConsumedAtUtc = consumedAtUtc;
    }
}
