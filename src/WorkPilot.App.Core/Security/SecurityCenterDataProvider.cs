using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Application.Automation;
using WorkPilot.Application.Permission.Policy;
using WorkPilot.Application.Security;
using WorkPilot.Application.Security.Governance;
using WorkPilot.Application.Security.Retention;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Domain.Security.Retention;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.PermissionGovernance;
using WorkPilot.Domain.Security;
using WorkPilot.Domain.Security.Audit;

namespace WorkPilot.App.Core.Security;

/// <summary>
/// Default <see cref="ISecurityCenterDataProvider"/>. Composes the Application governance services and
/// stores behind the single BCL boundary (doc 06 §1). It performs only orchestration and projection —
/// all persistence, secret handling and native calls live behind the injected ports. The WinUI
/// composition root (T20c) supplies the concrete ports; this type is the BCL-side glue.
/// </summary>
public sealed class SecurityCenterDataProvider : ISecurityCenterDataProvider
{
    private const string EmergencyStopKey = "emergency_stop";
    private const int PostureIncidentLimit = 10_000;
    private const int AuditBrowseCap = 10_000;

    private readonly IIncidentStore _incidents;
    private readonly IncidentGovernanceService _incidentGov;
    private readonly ISourceGovernanceBackend _sourceBackend;
    private readonly SourceGovernanceService _sourceGov;
    private readonly IGrantStore _grants;
    private readonly GrantGovernanceService _grantGov;
    private readonly ISecurityStateStore _state;
    private readonly EmergencyStopCoordinator _emergency;
    private readonly IAuditLogStore _audit;
    private readonly RetentionSettingsService _retentionSettings;
    private readonly DataRetentionCleaner _cleaner;
    private readonly SupportBundleBuilder _supportBuilder;
    private readonly IRunReportExporter _runReportExporter;

    public SecurityCenterDataProvider(
        IIncidentStore incidents,
        IncidentGovernanceService incidentGov,
        ISourceGovernanceBackend sourceBackend,
        SourceGovernanceService sourceGov,
        IGrantStore grants,
        GrantGovernanceService grantGov,
        ISecurityStateStore state,
        EmergencyStopCoordinator emergency,
        IAuditLogStore audit,
        RetentionSettingsService retentionSettings,
        DataRetentionCleaner cleaner,
        SupportBundleBuilder supportBuilder,
        IRunReportExporter runReportExporter)
    {
        _incidents = incidents;
        _incidentGov = incidentGov;
        _sourceBackend = sourceBackend;
        _sourceGov = sourceGov;
        _grants = grants;
        _grantGov = grantGov;
        _state = state;
        _emergency = emergency;
        _audit = audit;
        _retentionSettings = retentionSettings;
        _cleaner = cleaner;
        _supportBuilder = supportBuilder;
        _runReportExporter = runReportExporter;
    }

    public async Task<Result<SecurityPosture>> GetPostureAsync(CancellationToken ct = default)
    {
        // Emergency stop flag is a pure state read.
        var stop = await _state.GetAsync(EmergencyStopKey, ct);
        var emergencyActive = stop.IsSuccess && stop.Value is "true";

        // Source health + detection status. A backend failure means detection is degraded — we must
        // NOT swallow it and must NOT present 0 sources as "safe" (doc 06 §10).
        var detectionDegraded = false;
        Dictionary<SourceHealthStatus, int> sourceCounts = new();
        var health = await _sourceBackend.ListHealthAsync(ct);
        if (!health.IsSuccess)
        {
            detectionDegraded = true;
        }
        else
        {
            foreach (var h in health.Value!)
            {
                if (sourceCounts.TryGetValue(h.Status, out var c))
                    sourceCounts[h.Status] = c + 1;
                else
                    sourceCounts[h.Status] = 1;
            }
        }

        // Incident counts grouped by state (read-only projection of the incident store).
        var incidentCounts = new Dictionary<IncidentState, int>();
        var list = await _incidents.ListAsync(null, PostureIncidentLimit, ct);
        foreach (var i in list)
        {
            if (incidentCounts.TryGetValue(i.State, out var c))
                incidentCounts[i.State] = c + 1;
            else
                incidentCounts[i.State] = 1;
        }

        return Result<SecurityPosture>.Ok(new SecurityPosture(
            emergencyActive, detectionDegraded,
            incidentCounts, sourceCounts));
    }

    // ---- Incidents ----
    public async Task<Result<IReadOnlyList<Incident>>> ListIncidentsAsync(IncidentState? state, int limit, CancellationToken ct = default)
    {
        var list = await _incidents.ListAsync(state, limit, ct);
        return Result<IReadOnlyList<Incident>>.Ok(list);
    }
    public Task<Result<Incident>> GetIncidentAsync(IncidentId id, CancellationToken ct = default) =>
        WrapIncidentAsync(() => _incidents.GetByIdAsync(id, ct));
    public Task<Result> AcknowledgeIncidentAsync(IncidentId id, CancellationToken ct = default) =>
        _incidentGov.AcknowledgeAsync(id, ct);
    public Task<Result> MitigateIncidentAsync(IncidentId id, CancellationToken ct = default) =>
        _incidentGov.MitigateAsync(id, ct);
    public Task<Result> ResolveIncidentAsync(IncidentId id, IncidentResolutionCode code, string note, CancellationToken ct = default) =>
        _incidentGov.ResolveAsync(id, code, note, ct);

    // ---- Sources ----
    public Task<Result<IReadOnlyList<SourceHealth>>> ListSourceHealthAsync(CancellationToken ct = default) =>
        _sourceBackend.ListHealthAsync(ct);
    public Task<Result> DisableSourceAsync(string kind, string id, CancellationToken ct = default) =>
        _sourceGov.DisableSourceAsync(kind, id, ct);
    public Task<Result> RecoverSourceAsync(string kind, string id, CancellationToken ct = default) =>
        _sourceGov.RecoverSourceAsync(kind, id, ct);

    // ---- Grants ----
    public Task<Result<IReadOnlyList<PolicyGrant>>> ListActiveGrantsAsync(DateTimeOffset asOf, CancellationToken ct = default) =>
        _grants.ListActiveAsync(asOf, ct);
    public Task<Result<GrantRevokePreview>> PreviewRevokeAsync(PolicyGrantId id, CancellationToken ct = default) =>
        _grantGov.PreviewRevokeAsync(id, ct);
    public Task<Result> RevokeGrantAsync(PolicyGrantId id, string impactToken, CancellationToken ct = default) =>
        _grantGov.RevokeAsync(id, impactToken, ct);

    // ---- Emergency stop ----
    public async Task<Result<bool>> GetEmergencyStopAsync(CancellationToken ct = default)
    {
        var s = await _state.GetAsync(EmergencyStopKey, ct);
        return s.IsSuccess ? Result<bool>.Ok(s.Value is "true") : Result<bool>.Fail(s.Error!);
    }
    public Task<Result> EmergencyStopAsync(string actor, CancellationToken ct = default) =>
        _emergency.StopAsync(actor, ct);
    public Task<Result> EmergencyResumeAsync(string actor, CancellationToken ct = default) =>
        _emergency.ResumeAsync(actor, ct);

    // ---- Audit ----
    public async Task<Result<IReadOnlyList<AuditEntry>>> QueryAuditAsync(AuditQuery query, CancellationToken ct = default)
    {
        var all = await _audit.GetAllAsync(ct);

        IEnumerable<AuditEntry> q = all;
        if (query.Category is not null) q = q.Where(e => e.Category == query.Category);
        if (!string.IsNullOrEmpty(query.Action)) q = q.Where(e => e.Action == query.Action);
        if (!string.IsNullOrEmpty(query.Actor)) q = q.Where(e => e.Actor == query.Actor);
        if (query.FromUtc is not null) q = q.Where(e => e.OccurredAtUtc >= query.FromUtc);
        if (query.ToUtc is not null) q = q.Where(e => e.OccurredAtUtc <= query.ToUtc);
        q = q.OrderByDescending(e => e.OccurredAtUtc).ThenByDescending(e => e.Sequence);

        var capped = q.Take(Math.Min(query.Limit, AuditBrowseCap)).ToList();
        return Result<IReadOnlyList<AuditEntry>>.Ok(capped);
    }

    // ---- Retention & export (doc 05 §9/§10, LOG-005/006, SEC-106/108) ----
    public async Task<Result<RetentionSettings>> GetRetentionSettingsAsync(CancellationToken ct = default)
    {
        var r = await _retentionSettings.GetAsync(ct);
        return r.IsSuccess ? Result<RetentionSettings>.Ok(r.Value!) : Result<RetentionSettings>.Fail(r.Error!);
    }

    public async Task<Result> SaveRetentionSettingsAsync(RetentionSettings settings, CancellationToken ct = default)
    {
        // The service clamps the policy to the allowed range before persisting (doc 05 §9).
        var r = await _retentionSettings.SaveAsync(settings, ct);
        return r.IsSuccess ? Result.Success() : Result.Failure(r.Error!);
    }

    public async Task<Result<RetentionCleanupResult>> RunRetentionCleanupAsync(bool force, CancellationToken ct = default)
    {
        var r = force ? await _cleaner.RunNowAsync(ct) : await _cleaner.RunAsync(ct);
        return r.IsSuccess ? Result<RetentionCleanupResult>.Ok(r.Value!) : Result<RetentionCleanupResult>.Fail(r.Error!);
    }

    public async Task<Result<SupportBundleResult>> BuildSupportPackageAsync(SupportBundleRequest request, CancellationToken ct = default)
    {
        var r = await _supportBuilder.BuildAsync(request, ct);
        return r.IsSuccess ? Result<SupportBundleResult>.Ok(r.Value!) : Result<SupportBundleResult>.Fail(r.Error!);
    }

    public async Task<Result<RunReport>> ExportRunReportAsync(RunId runId, CancellationToken ct = default)
    {
        var r = await _runReportExporter.BuildAsync(runId, ct);
        return r.IsSuccess ? Result<RunReport>.Ok(r.Value!) : Result<RunReport>.Fail(r.Error!);
    }

    private async Task<Result<Incident>> WrapIncidentAsync(Func<Task<Incident?>> read)
    {
        var inc = await read();
        return inc is not null
            ? Result<Incident>.Ok(inc)
            : Result<Incident>.Fail(SecurityGovernanceErrors.IncidentNotFoundError("?"));
    }
}
