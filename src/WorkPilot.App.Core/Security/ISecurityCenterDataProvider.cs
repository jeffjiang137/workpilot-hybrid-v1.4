using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Application.Security.Governance;
using WorkPilot.Application.Security.Retention;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.PermissionGovernance;
using WorkPilot.Domain.Security;
using WorkPilot.Domain.Security.Audit;
using WorkPilot.Domain.Security.Retention;

namespace WorkPilot.App.Core.Security;

/// <summary>The six tabs of the Security Center (doc 06 §1).</summary>
public enum SecurityCenterTab
{
    Posture = 0,
    Incidents = 1,
    Sources = 2,
    Grants = 3,
    Audit = 4,
    Operations = 5
}

/// <summary>
/// Aggregated situational-awareness snapshot for the Posture tab (doc 06 §1/§7). The Security Center
/// is a governance read/command surface only �? it never touches connectors/MCP/run tables directly.
/// </summary>
public sealed record SecurityPosture(
    bool EmergencyStopActive,
    bool DetectionDegraded,
    IReadOnlyDictionary<IncidentState, int> IncidentCounts,
    IReadOnlyDictionary<SourceHealthStatus, int> SourceHealthCounts);

/// <summary>Filter for the Audit tab query (doc 06 §8). UI browse is capped by the provider.</summary>
public sealed record AuditQuery(
    AuditCategory? Category = null,
    string? Action = null,
    string? Actor = null,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    int Limit = 200);

/// <summary>
/// BCL facade the Security Center view models bind to. It wraps the Application governance services
/// and stores behind one boundary so the view models stay free of secrets, connectors, native calls
/// and repository details (AI dev rule §3). The WinUI page and the BCL unit tests both depend only on
/// this interface (doc 06 §1).
/// </summary>
public interface ISecurityCenterDataProvider
{
    // ---- Posture ----
    Task<Result<SecurityPosture>> GetPostureAsync(CancellationToken ct = default);

    // ---- Incidents (doc 06 §3) ----
    Task<Result<IReadOnlyList<Incident>>> ListIncidentsAsync(IncidentState? state, int limit, CancellationToken ct = default);
    Task<Result<Incident>> GetIncidentAsync(IncidentId id, CancellationToken ct = default);
    Task<Result> AcknowledgeIncidentAsync(IncidentId id, CancellationToken ct = default);
    Task<Result> MitigateIncidentAsync(IncidentId id, CancellationToken ct = default);
    Task<Result> ResolveIncidentAsync(IncidentId id, IncidentResolutionCode code, string note, CancellationToken ct = default);

    // ---- Sources (doc 06 §6.2 / §7) ----
    Task<Result<IReadOnlyList<SourceHealth>>> ListSourceHealthAsync(CancellationToken ct = default);
    Task<Result> DisableSourceAsync(string kind, string id, CancellationToken ct = default);
    Task<Result> RecoverSourceAsync(string kind, string id, CancellationToken ct = default);

    // ---- Grants (doc 06 §6.3 / PER-008) ----
    Task<Result<IReadOnlyList<PolicyGrant>>> ListActiveGrantsAsync(DateTimeOffset asOf, CancellationToken ct = default);
    Task<Result<GrantRevokePreview>> PreviewRevokeAsync(PolicyGrantId id, CancellationToken ct = default);
    Task<Result> RevokeGrantAsync(PolicyGrantId id, string impactToken, CancellationToken ct = default);

    // ---- Emergency stop (doc 06 §6.4) ----
    Task<Result<bool>> GetEmergencyStopAsync(CancellationToken ct = default);
    Task<Result> EmergencyStopAsync(string actor, CancellationToken ct = default);
    Task<Result> EmergencyResumeAsync(string actor, CancellationToken ct = default);

    // ---- Audit (doc 06 §8) ----
    Task<Result<IReadOnlyList<AuditEntry>>> QueryAuditAsync(AuditQuery query, CancellationToken ct = default);

    // ---- Retention & export (doc 05 §9/§10, LOG-005/006, SEC-106/108) ----
    Task<Result<RetentionSettings>> GetRetentionSettingsAsync(CancellationToken ct = default);
    Task<Result> SaveRetentionSettingsAsync(RetentionSettings settings, CancellationToken ct = default);
    Task<Result<RetentionCleanupResult>> RunRetentionCleanupAsync(bool force, CancellationToken ct = default);
    Task<Result<SupportBundleResult>> BuildSupportPackageAsync(SupportBundleRequest request, CancellationToken ct = default);
    Task<Result<RunReport>> ExportRunReportAsync(RunId runId, CancellationToken ct = default);
}
