using WorkPilot.Contracts.Primitives.Ids;

namespace WorkPilot.Domain.Automation.Scheduling;

/// <summary>
/// Pure overlap-policy decision (spec doc 04 §5, RUN-009). Given the automation's in-flight runs and
/// a new candidate, it decides what the materializer should do. Concurrency is fixed at 1, so at most
/// one active execution exists; claim-time enforcement is the materializer's job (T09). This type is
/// shared by preview (to explain what *would* happen) and the materializer (to act).
/// </summary>
public static class OverlapPolicyEvaluator
{
    public static OverlapDecision Evaluate(
        OverlapPolicy policy,
        IReadOnlyList<ExistingRunSummary> existingRuns,
        int nextCoalescedCount = 1)
    {
        var nonTerminal = existingRuns.Where(r => r.Status != RunStatusCategory.Terminal).ToArray();

        switch (policy)
        {
            case OverlapPolicy.Skip:
                return nonTerminal.Length > 0
                    ? new OverlapDecision(OverlapDecisionKind.Skip)          // occurrence=skipped_overlap
                    : new OverlapDecision(OverlapDecisionKind.Create);

            case OverlapPolicy.QueueOne:
            {
                if (nonTerminal.Length == 0)
                    return new OverlapDecision(OverlapDecisionKind.Create, CoalescedCount: 1);
                var queued = nonTerminal
                    .Where(r => r.Status == RunStatusCategory.Queued)
                    .OrderBy(r => r.Id.Value, StringComparer.Ordinal)
                    .ToArray();
                if (queued.Length >= 1)
                {
                    // Coalesce into the earliest queued run; bump its coalesced count (RUN-A04).
                    var target = queued[0];
                    var newCount = Math.Max(nextCoalescedCount, target.CancellationRequested ? 1 : nextCoalescedCount);
                    return new OverlapDecision(OverlapDecisionKind.Coalesce,
                        CoalesceTargetId: target.Id, CoalescedCount: newCount);
                }
                // A run is in-flight but nothing queued yet: create the single allowed queued run.
                return new OverlapDecision(OverlapDecisionKind.Create, CoalescedCount: 1);
            }

            case OverlapPolicy.CancelPrevious:
            default:
            {
                if (nonTerminal.Length > 0)
                {
                    var targets = nonTerminal.Select(r => r.Id).ToArray();
                    return new OverlapDecision(OverlapDecisionKind.CancelPreviousAndCreate,
                        CancellationTargetIds: targets);
                }
                return new OverlapDecision(OverlapDecisionKind.Create);
            }
        }
    }
}
