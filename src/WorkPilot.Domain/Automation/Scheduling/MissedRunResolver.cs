using WorkPilot.Contracts.Primitives;

namespace WorkPilot.Domain.Automation.Scheduling;

/// <summary>
/// Pure missed-run resolution (spec doc 04 §3, RUN-010). Given the last materialized instant and
/// "now", enumerates the occurrences the trigger should have produced in (lastMaterialized, now]
/// using the same <see cref="ScheduleCalculator"/>, then applies the missed-run policy. The
/// materializer (T09) persists the result; this type is deterministic and side-effect free.
/// </summary>
public static class MissedRunResolver
{
    public static MissedRunResult Resolve(
        TriggerDefinition trigger,
        DateTimeOffset lastMaterializedAt,
        DateTimeOffset now,
        MissedRunPolicy policy,
        ITimeZoneResolver resolver)
    {
        var candidates = new List<DateTimeOffset>();
        var cursor = lastMaterializedAt;
        // Enumerate occurrences strictly after `cursor`, stopping at `now`. Guard prevents runaway.
        for (var guard = 0; guard < 100_000; guard++)
        {
            var r = ScheduleCalculator.ComputeNext(trigger, cursor, resolver);
            if (!r.HasOccurrence) break;
            var occ = r.Occurrence!.Utc;
            if (occ > now) break;
            candidates.Add(occ);
            if (occ >= now) break;
            cursor = occ;
        }

        if (candidates.Count == 0)
            return new MissedRunResult(Array.Empty<DateTimeOffset>(), 0, null);

        var last = candidates[^1];
        switch (policy)
        {
            case MissedRunPolicy.Skip:
                // No historical runs; just advance. Count is recorded for telemetry (SCH-A08).
                return new MissedRunResult(Array.Empty<DateTimeOffset>(), candidates.Count, last);

            case MissedRunPolicy.RunOnce:
                // Only the most recent missed candidate becomes a run (SCH-A08).
                return new MissedRunResult(new[] { last }, candidates.Count - 1, last);

            case MissedRunPolicy.CatchUp:
            default:
            {
                var cap = Limits.V1_5.MaxCatchUpRuns; // RUN-010: catch up at most 5
                var take = Math.Min(cap, candidates.Count);
                var occurrences = candidates.GetRange(0, take);
                var skipped = Math.Max(0, candidates.Count - take); // SCH-A09: remainder summarized
                return new MissedRunResult(occurrences, skipped, last);
            }
        }
    }
}
