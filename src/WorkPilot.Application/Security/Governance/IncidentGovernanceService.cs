using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Security;

namespace WorkPilot.Application.Security.Governance;

/// <summary>
/// Lifecycle commands for an aggregated incident (doc 06 §3): acknowledge, mitigate, resolve.
/// Resolution is delegated to <see cref="IncidentAggregatorService.ResolveAsync"/> so the
/// notification gate resets and re-open alerts can fire again. No implicit clock — uses injected
/// <see cref="IClock"/> (AI dev rule: no static UtcNow).
/// </summary>
public sealed class IncidentGovernanceService
{
    private readonly IIncidentStore _incidents;
    private readonly IncidentAggregatorService _aggregator;
    private readonly IClock _clock;

    public IncidentGovernanceService(IIncidentStore incidents, IncidentAggregatorService aggregator, IClock clock)
    {
        _incidents = incidents;
        _aggregator = aggregator;
        _clock = clock;
    }

    public async Task<Result> AcknowledgeAsync(IncidentId id, CancellationToken ct)
    {
        var incident = await _incidents.GetByIdAsync(id, ct);
        if (incident is null) return Result.Failure(SecurityGovernanceErrors.IncidentNotFoundError(id.Value));
        if (incident.State != IncidentState.Open)
            return Result.Failure(SecurityGovernanceErrors.IncidentInvalidTransitionError(incident.State.ToString(), IncidentState.Acknowledged.ToString()));
        await _incidents.UpdateAsync(incident with { State = IncidentState.Acknowledged, UpdatedAtUtc = _clock.UtcNow }, ct);
        return Result.Success();
    }

    public async Task<Result> MitigateAsync(IncidentId id, CancellationToken ct)
    {
        var incident = await _incidents.GetByIdAsync(id, ct);
        if (incident is null) return Result.Failure(SecurityGovernanceErrors.IncidentNotFoundError(id.Value));
        if (incident.State is IncidentState.Resolved or IncidentState.Reopened)
            return Result.Failure(SecurityGovernanceErrors.IncidentInvalidTransitionError(incident.State.ToString(), IncidentState.Mitigated.ToString()));
        await _incidents.UpdateAsync(incident with { State = IncidentState.Mitigated, UpdatedAtUtc = _clock.UtcNow }, ct);
        return Result.Success();
    }

    public async Task<Result> ResolveAsync(IncidentId id, IncidentResolutionCode code, string? note, CancellationToken ct)
    {
        var incident = await _incidents.GetByIdAsync(id, ct);
        if (incident is null) return Result.Failure(SecurityGovernanceErrors.IncidentNotFoundError(id.Value));
        return await _aggregator.ResolveAsync(incident, code, note, ct);
    }
}
