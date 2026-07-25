using WorkPilot.Domain.Automation.Run;

namespace WorkPilot.Application.Automation.Run.Recovery;

/// <summary>
/// Tells recovery whether a capability provider supports an idempotency key (doc 04 §9). If it does,
/// a send that got no response can be safely retried with the SAME key; otherwise a write with an
/// unknown outcome must go to human review rather than be auto-replayed. Resolved from the step's
/// capability identity (the real Host implementation reads the adapter registry / run snapshot).
/// </summary>
public interface IProviderIdempotencyResolver
{
    bool SupportsIdempotency(StepRun step);
}
