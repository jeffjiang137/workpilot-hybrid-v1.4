using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Application.Security.Governance;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Security;
using WorkPilot.Domain.Security.Audit;

namespace WorkPilot.Application.Security.Retention;

/// <summary>Verdict from a disk-space check (SEC-107).</summary>
public sealed record DiskSpaceVerdict(
    bool Low,
    bool StopNewAutomation,
    bool CleanupTriggered,
    bool IncidentRaised,
    long FreeBytes);

/// <summary>
/// Disk-space guard (SEC-107). When free space on the data volume drops below
/// <see cref="Limits.V1_5.RetentionDiskLowThresholdBytes"/> (200 MiB) it: stops admitting new
/// automation (caller honours <see cref="DiskSpaceVerdict.StopNewAutomation"/>), triggers a best-effort
/// cleanup, and raises a single High incident (de-duplicated by a fixed fingerprint). Open events are
/// never auto-deleted. The verdict is pure data — the host decides how to enforce suspension.
/// </summary>
public sealed class DiskSpaceGuard
{
    private const string DiskLowFingerprint = "disk-space-low";
    private const string SuspendStateKey = "automation_suspended_disk_low";

    private readonly IDiskSpaceProbe _probe;
    private readonly DataRetentionCleaner _cleaner;
    private readonly IIncidentStore _incidents;
    private readonly IIdGenerator _ids;
    private readonly IClock _clock;
    private readonly ISecurityStateStore? _state;

    public DiskSpaceGuard(
        IDiskSpaceProbe probe,
        DataRetentionCleaner cleaner,
        IIncidentStore incidents,
        IIdGenerator ids,
        IClock clock,
        ISecurityStateStore? state = null)
    {
        _probe = probe;
        _cleaner = cleaner;
        _incidents = incidents;
        _ids = ids;
        _clock = clock;
        _state = state;
    }

    public async Task<Result<DiskSpaceVerdict>> CheckAsync(string path, CancellationToken ct = default)
    {
        var free = _probe.GetFreeBytes(path);
        var low = free < Limits.V1_5.RetentionDiskLowThresholdBytes;
        if (!low)
            return Result<DiskSpaceVerdict>.Ok(new DiskSpaceVerdict(false, false, false, false, free));

        // Best-effort cleanup; failure must not block the incident. Open events are NOT deleted by the
        // cleaner (only terminal runs / resolved incidents / aged audit), satisfying SEC-107.
        var cleanupTriggered = false;
        var cleanup = await _cleaner.RunNowAsync(ct).ConfigureAwait(false);
        if (cleanup.IsSuccess) cleanupTriggered = cleanup.Value!.Ran;

        var raised = await RaiseOrUpdateIncidentAsync(ct).ConfigureAwait(false);

        if (_state is not null)
        {
            try { await _state.SetAsync(SuspendStateKey, "1", ct).ConfigureAwait(false); }
            catch { /* non-fatal: host may not have wired state */ }
        }

        return Result<DiskSpaceVerdict>.Ok(new DiskSpaceVerdict(true, true, cleanupTriggered, raised, free));
    }

    private async Task<bool> RaiseOrUpdateIncidentAsync(CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var existing = await _incidents.GetOpenByFingerprintAsync(DiskLowFingerprint, now.AddDays(-1), ct)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            var updated = existing with
            {
                Count = existing.Count + 1,
                LastSeenUtc = now,
                UpdatedAtUtc = now
            };
            await _incidents.UpdateAsync(updated, ct).ConfigureAwait(false);
            return false; // already tracked
        }

        var incident = new Incident(
            Id: IncidentId.Create(_ids),
            Fingerprint: DiskLowFingerprint,
            State: IncidentState.Open,
            Severity: SecuritySeverity.High,
            Type: SecurityEventType.DiskSpaceLow,
            FirstSeenUtc: now,
            LastSeenUtc: now,
            Count: 1,
            RecentEvidenceDigests: Array.Empty<string>(),
            ResolutionCode: null,
            ResolutionNote: null,
            ResolvedAtUtc: null,
            CreatedAtUtc: now,
            UpdatedAtUtc: now,
            LastActionId: null);
        await _incidents.InsertAsync(incident, ct).ConfigureAwait(false);
        return true;
    }
}
