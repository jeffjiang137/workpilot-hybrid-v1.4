using System;

namespace WorkPilot.Application.Security.Retention;

/// <summary>Outcome of one retention cleanup pass (doc 05 §9, SEC-106).</summary>
public sealed record RetentionCleanupResult(
    bool Ran,
    bool SkippedBecauseAlreadyRunToday,
    int RunEventsDeleted,
    int RunsDeleted,
    int AuditRecordsDeleted,
    int IncidentsDeleted,
    DateTimeOffset CutoffUtc,
    DateTimeOffset? CompletedAtUtc,
    string? SkipReason)
{
    public static RetentionCleanupResult Skipped(string reason) =>
        new(false, true, 0, 0, 0, 0, default, null, reason);

    public static RetentionCleanupResult Executed(
        int runEvents, int runs, int audit, int incidents, DateTimeOffset cutoff, DateTimeOffset completed) =>
        new(true, false, runEvents, runs, audit, incidents, cutoff, completed, null);
}
