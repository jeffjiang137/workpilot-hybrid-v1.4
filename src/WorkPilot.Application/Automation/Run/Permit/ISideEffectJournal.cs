using System;
using System.Collections.Generic;
using WorkPilot.Domain.Automation.Run;

namespace WorkPilot.Application.Automation.Run.Permit;

/// <summary>
/// Durable-in-spirit record of where a side-effect step is in its pipeline. The executor writes each
/// phase transition here; crash recovery (T13) reads it to decide retry vs. needs_review. The in-process
/// implementation is for the sandbox; the Host backs this with the run_events / recovery journal.
/// </summary>
public interface ISideEffectJournal
{
    void Record(SideEffectPhaseRecord record);
    IReadOnlyList<SideEffectPhaseRecord> EntriesFor(string runId, string stepId);
}

/// <summary>Default in-memory <see cref="ISideEffectJournal"/> (sandbox + tests).</summary>
public sealed class InMemorySideEffectJournal : ISideEffectJournal
{
    private readonly List<SideEffectPhaseRecord> _entries = new();
    private readonly object _gate = new();

    public void Record(SideEffectPhaseRecord record)
    {
        lock (_gate) _entries.Add(record);
    }

    public IReadOnlyList<SideEffectPhaseRecord> EntriesFor(string runId, string stepId)
    {
        lock (_gate) return _entries.FindAll(e => e.RunId == runId && e.StepId == stepId);
    }
}

