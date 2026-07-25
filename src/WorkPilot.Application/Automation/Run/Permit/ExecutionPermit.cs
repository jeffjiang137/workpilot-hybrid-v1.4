using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Domain.Automation.Run;

namespace WorkPilot.Application.Automation.Run.Permit;

/// <summary>
/// Opaque single-use permit handle minted only by <see cref="ManagedPermitCore"/> (or a native
/// equivalent). C# callers receive it but can never reconstruct its signature, so forging a permit is
/// impossible. Consuming is the send-time current-state check.
/// </summary>
internal sealed class ExecutionPermit : IExecutionPermit
{
    private readonly ManagedPermitCore _core;
    private readonly string _permitId;
    private readonly string _signature;
    private readonly PermitBinding _binding;

    public ExecutionPermit(ManagedPermitCore core, string permitId, string signature, PermitBinding binding)
    {
        _core = core;
        _permitId = permitId;
        _signature = signature;
        _binding = binding;
    }

    public bool IsConsumed { get; private set; }

    public Task<Result<PermitConsumption>> ConsumeAndCheckAsync(PermitLiveState live, CancellationToken ct = default)
    {
        var result = _core.ConsumeAndCheck(_permitId, _signature, _binding, live);
        if (result.IsSuccess) IsConsumed = true;
        return Task.FromResult(result);
    }

    public void Revoke() => _core.Revoke(_permitId);
}
