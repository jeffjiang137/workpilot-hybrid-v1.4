using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Domain.Security;
using WorkPilot.Domain.Security.Audit;

namespace WorkPilot.Application.Security.Retention;

/// <summary>
/// Drives a retention cleanup pass (doc 05 §9, SEC-106).
/// <list type="bullet">
///   <item><description>Runs at most once per UTC calendar day (enforced via <see cref="RetentionSettings.LastCleanupAtUtc"/>).</description></item>
///   <item><description>Deletes in bounded batches of <see cref="Limits.V1_5.RetentionCleanupBatchSize"/>; yields between batches.</description></item>
///   <item><description>Order: run events → terminal runs (cascade) → audit records → resolved incidents.</description></item>
///   <item><description>Never deletes non-terminal runs (waiting-approval / needs-review) or open incidents — the store only returns eligible rows.</description></item>
///   <item><description>Writes one audit entry with category/counts/cutoff and NO business IDs, then stamps <see cref="RetentionSettings.LastCleanupAtUtc"/>.</description></item>
/// </list>
/// </summary>
public sealed class DataRetentionCleaner
{
    private readonly IRetentionSettingsStore _settings;
    private readonly IRetentionStore _store;
    private readonly AuditLogWriter _audit;
    private readonly IClock _clock;

    public DataRetentionCleaner(
        IRetentionSettingsStore settings,
        IRetentionStore store,
        AuditLogWriter audit,
        IClock clock)
    {
        _settings = settings;
        _store = store;
        _audit = audit;
        _clock = clock;
    }

    /// <summary>Honours the daily-once rule. Use <see cref="RunNowAsync"/> to force (tests / manual trigger).</summary>
    public Task<Result<RetentionCleanupResult>> RunAsync(CancellationToken ct = default) => RunCoreAsync(force: false, ct);

    /// <summary>Force a cleanup even if one already ran today (manual "clean now" / disk-pressure path).</summary>
    public Task<Result<RetentionCleanupResult>> RunNowAsync(CancellationToken ct = default) => RunCoreAsync(force: true, ct);

    private async Task<Result<RetentionCleanupResult>> RunCoreAsync(bool force, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var get = await _settings.GetAsync(ct).ConfigureAwait(false);
        if (!get.IsSuccess) return Result<RetentionCleanupResult>.Fail(get.Error!);
        var settings = get.Value!;

        if (!force && settings.CleanupAlreadyRunToday(now))
            return Result<RetentionCleanupResult>.Ok(RetentionCleanupResult.Skipped("already_run_today"));

        var (runCutoff, eventCutoff, auditCutoff) = settings.Policy.ComputeCutoffs(now);
        var batch = Limits.V1_5.RetentionCleanupBatchSize;

        try
        {
            var runEvents = await DeleteCountLoop(
                c => _store.DeleteRunEventsOlderThanAsync(eventCutoff, batch, c), ct).ConfigureAwait(false);
            if (!runEvents.IsSuccess) return Result<RetentionCleanupResult>.Fail(runEvents.Error!);

            var runs = await DeleteTerminalRunsAsync(runCutoff, batch, ct).ConfigureAwait(false);
            if (!runs.IsSuccess) return Result<RetentionCleanupResult>.Fail(runs.Error!);

            var auditRecs = await DeleteCountLoop(
                c => _store.DeleteAuditRecordsOlderThanAsync(auditCutoff, batch, c), ct).ConfigureAwait(false);
            if (!auditRecs.IsSuccess) return Result<RetentionCleanupResult>.Fail(auditRecs.Error!);

            var incidents = await DeleteCountLoop(
                c => _store.DeleteResolvedIncidentsOlderThanAsync(auditCutoff, batch, c), ct).ConfigureAwait(false);
            if (!incidents.IsSuccess) return Result<RetentionCleanupResult>.Fail(incidents.Error!);

            await WriteCleanupAuditAsync(runEvents.Value, runs.Value, auditRecs.Value, incidents.Value, eventCutoff, runCutoff, auditCutoff, ct)
                .ConfigureAwait(false);

            var stamped = settings with { LastCleanupAtUtc = now };
            var save = await _settings.SaveAsync(stamped, ct).ConfigureAwait(false);
            if (!save.IsSuccess) return Result<RetentionCleanupResult>.Fail(save.Error!);

            return Result<RetentionCleanupResult>.Ok(RetentionCleanupResult.Executed(
                runEvents.Value, runs.Value, auditRecs.Value, incidents.Value, runCutoff, now));
        }
        catch (Exception ex)
        {
            return Result<RetentionCleanupResult>.Fail(RetentionAndExportErrors.CleanupFailedError(ex.Message));
        }
    }

    private async Task<Result<int>> DeleteCountLoop(
        Func<CancellationToken, Task<Result<int>>> delete, CancellationToken ct)
    {
        var total = 0;
        for (var b = 0; b < Limits.V1_5.RetentionCleanupMaxBatchesPerRun; b++)
        {
            var r = await delete(ct).ConfigureAwait(false);
            if (!r.IsSuccess) return Result<int>.Fail(r.Error!);
            total += r.Value!;
            if (r.Value < Limits.V1_5.RetentionCleanupBatchSize) break;
            await Task.Yield();
        }
        return Result<int>.Ok(total);
    }

    private async Task<Result<int>> DeleteTerminalRunsAsync(DateTimeOffset runCutoff, int batch, CancellationToken ct)
    {
        var total = 0;
        for (var b = 0; b < Limits.V1_5.RetentionCleanupMaxBatchesPerRun; b++)
        {
            var idsRes = await _store.GetDeletableRunIdsAsync(runCutoff, batch, ct).ConfigureAwait(false);
            if (!idsRes.IsSuccess) return Result<int>.Fail(idsRes.Error!);
            var ids = idsRes.Value!;
            if (ids.Count == 0) break;
            foreach (var id in ids)
            {
                var d = await _store.DeleteRunCascadeAsync(id, ct).ConfigureAwait(false);
                if (!d.IsSuccess) return Result<int>.Fail(d.Error!);
                total += d.Value!;
            }
            await Task.Yield();
        }
        return Result<int>.Ok(total);
    }

    private Task<Result<AuditEntry>> WriteCleanupAuditAsync(
        int runEvents, int runs, int auditRecs, int incidents,
        DateTimeOffset eventCutoff, DateTimeOffset runCutoff, DateTimeOffset auditCutoff, CancellationToken ct)
    {
        // Subject is generic — NEVER carries business IDs (SEC-106).
        var subject = JsonSerializer.Serialize(new { kind = "retention_cleanup" });
        var detail = JsonSerializer.Serialize(new
        {
            run_events_deleted = runEvents,
            runs_deleted = runs,
            audit_records_deleted = auditRecs,
            incidents_deleted = incidents,
            event_cutoff_utc = eventCutoff.UtcDateTime.ToString("O"),
            run_cutoff_utc = runCutoff.UtcDateTime.ToString("O"),
            audit_cutoff_utc = auditCutoff.UtcDateTime.ToString("O")
        });
        return _audit.AppendAsync(AuditCategory.System, "retention_cleanup", "system", subject, "", detail, ct);
    }
}
