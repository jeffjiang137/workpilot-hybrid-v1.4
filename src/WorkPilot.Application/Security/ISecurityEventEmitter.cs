using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Domain.Security;

namespace WorkPilot.Application.Security;

/// <summary>
/// Port that records an emitted security event. The concrete implementation (T19d) persists it and
/// drives incident aggregation. Used by detectors and the audit-integrity monitor so neither needs
/// to know about storage or aggregation internals.
/// </summary>
public interface ISecurityEventEmitter
{
    Task EmitAsync(SecurityEvent e, CancellationToken ct);
}
