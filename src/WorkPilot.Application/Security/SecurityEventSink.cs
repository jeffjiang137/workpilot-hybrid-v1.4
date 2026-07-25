using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Domain.Security;

namespace WorkPilot.Application.Security;

/// <summary>
/// <see cref="ISecurityEventEmitter"/> that routes emitted events into
/// <see cref="IncidentAggregatorService"/>. This is what the detector engine writes to, so every
/// detected event is persisted and folded into its incident.
/// </summary>
public sealed class SecurityEventSink : ISecurityEventEmitter
{
    private readonly IncidentAggregatorService _service;
    public SecurityEventSink(IncidentAggregatorService service) => _service = service;
    public Task EmitAsync(SecurityEvent e, CancellationToken ct) => _service.ProcessEventAsync(e, ct);
}
