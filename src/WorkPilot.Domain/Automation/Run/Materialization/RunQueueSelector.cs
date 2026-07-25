using System.Collections.Generic;
using System.Linq;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;

namespace WorkPilot.Domain.Automation.Run.Materialization;

/// <summary>One queued run as seen by the claim planner (spec doc 04 §6).</summary>
public sealed record QueuedRunInfo(
    RunId Id,
    AutomationId AutomationId,
    int Priority,
    DateTimeOffset ScheduledAtUtc,
    DateTimeOffset AvailableAtUtc);

/// <summary>
/// Pure claim-plan selector (spec doc 04 §6, RUN-002). Given the currently claimable queued runs,
/// it decides which to claim this tick. Ordering is priority DESC, then scheduled-at ASC, then id ASC
/// (so the most important and the most overdue run wins). A global slot limit caps total concurrency
/// (default 2, hard cap 4 per spec §6); a per-automation cap (fixed at 1) prevents two runs of the
/// same automation from being claimed simultaneously. The database <c>ClaimBatchAsync</c> re-checks
/// these invariants under a write lock, so this selector is the deterministic plan and the SQL is the
/// authoritative guard. Side-effect free and fully unit-testable.
/// </summary>
public static class RunQueueSelector
{
    public const int DefaultGlobalSlots = 2;
    public const int MaxGlobalSlots = 4;
    public const int PerAutomationConcurrency = 1;

    public static IReadOnlyList<RunId> Select(
        IReadOnlyList<QueuedRunInfo> queued,
        int availableSlots,
        int perAutomationConcurrency = PerAutomationConcurrency)
    {
        if (queued is null) throw new System.ArgumentNullException(nameof(queued));
        if (availableSlots < 0) availableSlots = 0;
        if (perAutomationConcurrency < 1) perAutomationConcurrency = 1;

        var ordered = queued
            .OrderByDescending(r => r.Priority)
            .ThenBy(r => r.ScheduledAtUtc)
            .ThenBy(r => r.Id.Value, System.StringComparer.Ordinal)
            .ToList();

        var chosen = new List<RunId>();
        var perAutomation = new Dictionary<string, int>();

        foreach (var r in ordered)
        {
            if (chosen.Count >= availableSlots) break;
            var key = r.AutomationId.Value;
            var used = perAutomation.TryGetValue(key, out var c) ? c : 0;
            if (used >= perAutomationConcurrency) continue;
            chosen.Add(r.Id);
            perAutomation[key] = used + 1;
        }

        return chosen;
    }
}
