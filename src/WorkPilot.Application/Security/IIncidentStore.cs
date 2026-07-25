using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Security;

namespace WorkPilot.Application.Security;

/// <summary>Persistence port for security events and aggregated incidents (SEC-102/103).</summary>
public interface ISecurityEventStore
{
    Task<Result> AppendAsync(SecurityEvent e, CancellationToken ct);
    Task<IReadOnlyList<SecurityEvent>> ListRecentAsync(int limit, CancellationToken ct);
    Task<bool> ExistsRecentAsync(string fingerprint, DateTimeOffset since, CancellationToken ct);
}

/// <summary>Persistence port for aggregated incidents (doc 06 §3).</summary>
public interface IIncidentStore
{
    /// <summary>Most recent open/reopened incident for a fingerprint seen at/after <paramref name="since"/>.</summary>
    Task<Incident?> GetOpenByFingerprintAsync(string fingerprint, DateTimeOffset since, CancellationToken ct);
    /// <summary>Incident by id (for governance commands that act on a specific incident).</summary>
    Task<Incident?> GetByIdAsync(IncidentId id, CancellationToken ct);
    Task InsertAsync(Incident incident, CancellationToken ct);
    Task UpdateAsync(Incident incident, CancellationToken ct);
    Task<IReadOnlyList<Incident>> ListAsync(IncidentState? state, int limit, CancellationToken ct);
}
