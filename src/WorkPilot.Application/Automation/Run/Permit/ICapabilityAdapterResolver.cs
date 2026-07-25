namespace WorkPilot.Application.Automation.Run.Permit;

/// <summary>
/// Resolves a capability identity (source kind / source id / stable id) to its adapter. Returns
/// <c>null</c> when the capability is not allowlisted / not registered, in which case the executor
/// routes the step to <see cref="WorkPilot.Domain.Automation.Run.StepRunStatus.BlockedPolicy"/>.
/// </summary>
public interface ICapabilityAdapterResolver
{
    ICapabilityAdapter? Resolve(string sourceKind, string sourceId, string stableId);
}
