using WorkPilot.Domain.Automation.Scheduling;

namespace WorkPilot.Domain.Automation.Run.Materialization;

/// <summary>
/// Maps a durable <see cref="RunStatus"/> to the coarse <see cref="RunStatusCategory"/> the
/// materializer/claim logic reasons about (spec doc 04 §5/§6). Terminal states collapse to
/// <see cref="RunStatusCategory.Terminal"/>; <c>Queued</c> is its own category so the per-automation
/// concurrency rule ("at most one active execution") can treat a still-queued run as eligible for
/// coalescing while a running/claimed run blocks new materialization.
/// </summary>
public static class RunStatusCategoryExtensions
{
    public static RunStatusCategory ToCategory(this RunStatus status) => status switch
    {
        RunStatus.Queued => RunStatusCategory.Queued,
        RunStatus.Claimed or RunStatus.Running or RunStatus.WaitingDelay or RunStatus.WaitingApproval
            => RunStatusCategory.Active,
        _ => RunStatusCategory.Terminal
    };
}
