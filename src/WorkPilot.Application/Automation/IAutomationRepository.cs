using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation;

namespace WorkPilot.Application.Automation;

/// <summary>Persistence port for automations. Implemented by Infrastructure; depends only on Domain + Contracts.</summary>
public interface IAutomationRepository
{
    Task<Result<AutomationDefinition>> GetAsync(AutomationId id, CancellationToken ct = default);
    Task<Result<IReadOnlyList<AutomationDefinition>>> ListBySpaceAsync(SpaceId spaceId, bool includeDeleted, CancellationToken ct = default);
    Task<Result<IReadOnlyList<AutomationRevision>>> GetRevisionsAsync(AutomationId id, CancellationToken ct = default);
    Task<Result<AutomationRevision>> GetRevisionAsync(AutomationRevisionId revisionId, CancellationToken ct = default);
    /// <summary>All automations currently in the <c>Enabled</c> lifecycle (used by EmergencyStop).</summary>
    Task<Result<IReadOnlyList<AutomationDefinition>>> ListEnabledAsync(CancellationToken ct = default);
    Task<Result<AutomationDefinition>> SaveAsync(AutomationDefinition definition, AutomationRevision? newRevision, CancellationToken ct = default);
}
