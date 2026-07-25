using System;
using System.Collections.Generic;

namespace WorkPilot.Domain.Automation.Run;

/// <summary>One recorded step in the side-effect pipeline (doc 04 §9). Lives in Domain so the recovery
/// planner (and any crash-recovery reader) can consume it without depending on the Application layer.</summary>
public sealed record SideEffectPhaseRecord(
    string RunId,
    string StepId,
    SideEffectPhase Phase,
    DateTimeOffset AtUtc,
    string? Detail = null);

/// <summary>Helpers for reading the side-effect journal (doc 04 §9).</summary>
public static class SideEffectJournalReader
{
    /// <summary>Returns the latest recorded phase for a step, or <c>null</c> if none recorded.
    /// Ties on timestamp resolve to the later-appended (most recent) entry.</summary>
    public static SideEffectPhase? LastPhase(IReadOnlyList<SideEffectPhaseRecord> entries)
    {
        SideEffectPhase? last = null;
        var lastAt = default(DateTimeOffset);
        foreach (var e in entries)
        {
            if (last is null || e.AtUtc >= lastAt)
            {
                last = e.Phase;
                lastAt = e.AtUtc;
            }
        }
        return last;
    }
}
