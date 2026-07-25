using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Application.Permission.Policy;
using WorkPilot.Contracts.Primitives;

namespace WorkPilot.Application.Security.Governance;

/// <summary>
/// Source lifecycle commands (doc 06 §6.2 / §7): disable and recover a connector/MCP source.
/// Disable bumps the revocation epoch (so the source's grants go stale) and asks the host backend
/// to disable + terminate the server. If any backend sub-action fails, the command still records
/// what succeeded and returns <see cref="SecurityGovernanceErrors.PartialFailureError"/> so the UI can
/// show precisely which sub-actions failed and offer a safe retry (doc 06 §10). Recovery is
/// explicit and requires the host's health probe; this service only flips the enabled flag back.
/// </summary>
public sealed class SourceGovernanceService
{
    private readonly ISourceGovernanceBackend _backend;
    private readonly IRevocationEpoch _epoch;

    public SourceGovernanceService(ISourceGovernanceBackend backend, IRevocationEpoch epoch)
    {
        _backend = backend;
        _epoch = epoch;
    }

    public async Task<Result> DisableSourceAsync(string sourceKind, string sourceId, CancellationToken ct)
    {
        var failures = new List<string>();

        var disable = await _backend.SetSourceEnabledAsync(sourceKind, sourceId, false, ct);
        if (!disable.IsSuccess) failures.Add($"disable:{disable.Error?.Code ?? "unknown"}");

        var terminate = await _backend.TerminateAsync(sourceKind, sourceId, ct);
        if (!terminate.IsSuccess) failures.Add($"terminate:{terminate.Error?.Code ?? "unknown"}");

        // Defense-in-depth: invalidate the source's grants even if a backend sub-action failed.
        _epoch.Bump();

        return failures.Count == 0
            ? Result.Success()
            : Result.Failure(SecurityGovernanceErrors.PartialFailureError(string.Join("; ", failures)));
    }

    public async Task<Result> RecoverSourceAsync(string sourceKind, string sourceId, CancellationToken ct)
    {
        var enable = await _backend.SetSourceEnabledAsync(sourceKind, sourceId, true, ct);
        return enable.IsSuccess ? Result.Success() : Result.Failure(enable.Error!);
    }
}
