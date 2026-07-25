using System;
using WorkPilot.Contracts.Primitives.Ids;

namespace WorkPilot.Domain.Security.Retention;

/// <summary>
/// Singleton retention settings (doc 05 §9, SEC-106). Persisted in the <c>retention_settings</c>
/// table (singleton row id = 1). <see cref="LastCleanupAtUtc"/> records the most recent successful
/// cleanup so the cleaner honours the "at most once per day" rule.
/// </summary>
public sealed record RetentionSettings(
    RetentionPolicy Policy,
    DateTimeOffset? LastCleanupAtUtc)
{
    public static readonly RetentionSettings Default = new(RetentionPolicy.Default, null);

    /// <summary>True when a cleanup already ran on the same UTC calendar day as <paramref name="now"/>.</summary>
    public bool CleanupAlreadyRunToday(DateTimeOffset now) =>
        LastCleanupAtUtc is { } last
        && last.Year == now.Year
        && last.Month == now.Month
        && last.Day == now.Day;
}
