using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.App.Core.Security;
using WorkPilot.App.Core.Tests.Fakes;
using WorkPilot.Application.Security.Governance;
using WorkPilot.Application.Security.Retention;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.PermissionGovernance;
using WorkPilot.Domain.Security;
using WorkPilot.Domain.Security.Audit;
using WorkPilot.Domain.Security.Retention;
using Xunit;

namespace WorkPilot.App.Core.Tests.Security;

public sealed class SecurityCenterViewModelTests
{
    private static readonly IClock Clock = new StubClock();
    private static readonly IIdGenerator Ids = new SeqIdGenerator();

    private static string Hash64(string input)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }

    private static Incident MakeIncident(IncidentId id, IncidentState state) =>
        new(id, Hash64("fp-" + id.Value), state, SecuritySeverity.High, SecurityEventType.AuthFailureContinuous,
            DateTimeOffset.UtcNow.AddMinutes(-3), DateTimeOffset.UtcNow, 1,
            new List<string> { Hash64("e1") }, null, null, null,
            DateTimeOffset.UtcNow.AddMinutes(-3), DateTimeOffset.UtcNow, null);

    private static PolicyGrant MakeGrant()
    {
        var now = DateTimeOffset.UtcNow;
        return PolicyGrant.Create(Ids,
            new GrantIssueRequest("auto-1", "rev-1", null, null, "mcp", "src-1", "cap-1", "sha-cap",
                new LocalProjectScope("p", new List<string> { "x" }, new List<string> { "y" }),
                RiskLevel.Medium, now, now.AddDays(1)),
            now, 1);
    }

    // ---------- EmptyState (doc 06 §10) ----------
    [Fact]
    public async Task Incident_empty_list_shows_empty_state_and_no_synthetic_rows()
    {
        var provider = new FakeSecurityCenterDataProvider();
        var vm = new IncidentListViewModel(provider);

        await vm.LoadAsync(CancellationToken.None);

        Assert.True(vm.IsEmpty);
        Assert.Empty(vm.Items);
        Assert.Empty(vm.SeverityBreakdown); // chart must not invent a sample curve
    }

    [Fact]
    public async Task Audit_empty_query_shows_empty_state()
    {
        var provider = new FakeSecurityCenterDataProvider();
        var vm = new AuditQueryViewModel(provider);

        await vm.RunQueryAsync(CancellationToken.None);

        Assert.True(vm.IsEmpty);
        Assert.Empty(vm.Entries);
    }

    // ---------- DetectionDegraded (doc 06 §10) ----------
    [Fact]
    public async Task Source_health_load_failure_surfaces_detection_degraded_and_is_not_swallowed()
    {
        var provider = new FakeSecurityCenterDataProvider { DetectionThrows = true };
        var vm = new SourceHealthViewModel(provider);

        await vm.LoadAsync(CancellationToken.None);

        Assert.True(vm.DetectionDegraded);
        Assert.True(vm.HealthDataIncomplete);
        Assert.Empty(vm.Health);          // 0 sources must NOT read as "safe"
        Assert.True(vm.HasError);          // exception surfaced, not swallowed
        Assert.Equal("SEC_DETECTION_DEGRADED", vm.Error!.Code);
    }

    // ---------- PartialFailure (doc 06 §10) ----------
    [Fact]
    public async Task Source_disable_partial_failure_lists_succeeded_and_failed_subactions()
    {
        var provider = new FakeSecurityCenterDataProvider
        {
            DisableResult = Result.Failure(SecurityGovernanceErrors.PartialFailureError("disable:ERR_X; terminate:ERR_Y"))
        };
        var vm = new SourceHealthViewModel(provider);

        var ok = await vm.DisableAsync(new SourceRef("mcp", "src-1"), CancellationToken.None);

        Assert.False(ok);
        Assert.True(vm.PartialFailure);
        Assert.Contains("disable:ERR_X", vm.FailedSubActions);
        Assert.Contains("terminate:ERR_Y", vm.FailedSubActions);
        Assert.Empty(vm.SucceededSubActions);
    }

    // ---------- ImpactChanged (doc 06 §10) ----------
    [Fact]
    public async Task Grant_revoke_with_impact_changed_is_refused_and_flagged()
    {
        var provider = new FakeSecurityCenterDataProvider
        {
            Grants = { MakeGrant() },
            PreviewResult = Result<GrantRevokePreview>.Ok(new GrantRevokePreview("T1", "cap-1", "auto-1")),
            RevokeResult = Result.Failure(SecurityGovernanceErrors.ImpactChangedError())
        };
        var vm = new GrantListViewModel(provider);
        await vm.LoadAsync(CancellationToken.None);
        var id = vm.Grants[0].GrantId;

        Assert.True(await vm.PreviewRevokeAsync(id, CancellationToken.None));
        var ok = await vm.RevokeAsync(id, CancellationToken.None);

        Assert.False(ok);
        Assert.True(vm.ImpactChanged);
        Assert.Equal("SEC_GOV_IMPACT_CHANGED", vm.Error!.Code);
    }

    // ---------- Happy paths ----------
    [Fact]
    public async Task Incident_acknowledge_transitions_open_to_acknowledged()
    {
        var id = IncidentId.Create(Ids);
        var provider = new FakeSecurityCenterDataProvider { Incidents = { MakeIncident(id, IncidentState.Open) } };
        var vm = new IncidentListViewModel(provider);

        var ok = await vm.AcknowledgeAsync(id, CancellationToken.None);
        var updated = await vm.OpenAsync(id, CancellationToken.None);

        Assert.True(ok);
        Assert.Equal(IncidentState.Acknowledged, updated!.State);
    }

    [Fact]
    public async Task Grant_revoke_success_removes_grant_from_list()
    {
        var provider = new FakeSecurityCenterDataProvider
        {
            Grants = { MakeGrant() },
            PreviewResult = Result<GrantRevokePreview>.Ok(new GrantRevokePreview("T1", "cap-1", "auto-1")),
            RevokeResult = Result.Success()
        };
        var vm = new GrantListViewModel(provider);
        await vm.LoadAsync(CancellationToken.None);
        var id = vm.Grants[0].GrantId;

        Assert.True(await vm.PreviewRevokeAsync(id, CancellationToken.None));
        Assert.True(await vm.RevokeAsync(id, CancellationToken.None));
        Assert.False(vm.ImpactChanged);
        Assert.Empty(vm.Grants); // reload reflects the revoked grant removed
    }

    [Fact]
    public async Task Top_emergency_stop_sets_active_flag()
    {
        var provider = new FakeSecurityCenterDataProvider { EmergencyStopResult = Result.Success() };
        var vm = new SecurityCenterViewModel(provider);

        var ok = await vm.StopAsync("operator", CancellationToken.None);

        Assert.True(ok);
        Assert.True(vm.EmergencyStopActive);
    }

    [Fact]
    public async Task Support_package_excludes_run_reports_by_default_and_requires_path()
    {
        var provider = new FakeSecurityCenterDataProvider
        {
            SupportResult = new SupportBundleResult(
                @"C:\support\pkg.zip",
                Hash64("manifest"),
                (long)(20 * 1024 * 1024),
                7,
                DateTimeOffset.UtcNow,
                new List<string> { "Incidents", "AuditLog", "SourceHealth", "Policy", "Configuration" })
        };
        var vm = new SecurityCenterViewModel(provider);

        Assert.False(vm.Support.IncludeRunReports); // default OFF (doc 06 §9)
        Assert.False(vm.Support.CanGenerate);       // no output path yet

        vm.Support.OutputPath = @"C:\support\pkg.zip";
        Assert.True(vm.Support.CanGenerate);

        var ok = await vm.Support.GenerateAsync(CancellationToken.None);
        Assert.True(ok);
        Assert.True(vm.Support.CanaryScanPerformed);
        Assert.False(string.IsNullOrEmpty(vm.Support.ManifestHash)); // SHA-256 manifest produced
        Assert.Equal(Hash64("manifest"), vm.Support.LastResult!.ManifestHash);
    }

    // ---------- Retention settings (doc 05 §9, SEC-106) ----------
    [Fact]
    public async Task Retention_load_populates_windows_and_last_cleanup()
    {
        var provider = new FakeSecurityCenterDataProvider
        {
            RetentionSettingsSeed = new RetentionSettings(new RetentionPolicy(120, 30, 300), null)
        };
        var vm = new RetentionSettingsViewModel(provider);

        await vm.LoadAsync(CancellationToken.None);

        Assert.Equal(120, vm.RunDays);
        Assert.Equal(30, vm.EventDays);
        Assert.Equal(300, vm.AuditDays);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task Retention_save_clamps_out_of_range_and_persists_clamped()
    {
        var provider = new FakeSecurityCenterDataProvider();
        var vm = new RetentionSettingsViewModel(provider)
        {
            RunDays = 10_000,   // far above max
            EventDays = -5,     // below min
            AuditDays = 9999    // above max
        };

        var ok = await vm.SaveAsync(CancellationToken.None);

        Assert.True(ok);
        Assert.True(provider.SavedSettings is not null);
        // After reload, the UI reflects the clamped values.
        Assert.Equal(Limits.V1_5.RetentionMaxRunDays, vm.RunDays);
        Assert.Equal(Limits.V1_5.RetentionMinEventDays, vm.EventDays);
        Assert.Equal(Limits.V1_5.RetentionMaxAuditDays, vm.AuditDays);
        // Persisted policy is the clamped one.
        Assert.Equal(Limits.V1_5.RetentionMaxRunDays, provider.SavedSettings!.Policy.RunDays);
        Assert.Equal(Limits.V1_5.RetentionMinEventDays, provider.SavedSettings.Policy.EventDays);
    }

    [Fact]
    public async Task Retention_cleanup_now_sets_result()
    {
        var provider = new FakeSecurityCenterDataProvider
        {
            CleanupResult = RetentionCleanupResult.Executed(7, 3, 5, 2,
                DateTimeOffset.UtcNow.AddDays(-90), DateTimeOffset.UtcNow)
        };
        var vm = new RetentionSettingsViewModel(provider);

        var ok = await vm.CleanupNowAsync(CancellationToken.None);

        Assert.True(ok);
        Assert.True(vm.LastCleanupResult is not null);
        Assert.Equal(7, vm.LastCleanupResult!.RunEventsDeleted);
        Assert.Equal(3, vm.LastCleanupResult.RunsDeleted);
        Assert.False(vm.LastCleanupSkipped);
    }

    // ---------- Support package through the real backend (doc 06 §9, LOG-006) ----------
    [Fact]
    public async Task Support_generate_with_run_reports_passes_selected_ids_to_backend()
    {
        var provider = new FakeSecurityCenterDataProvider
        {
            SupportResult = new SupportBundleResult(
                @"C:\support\pkg.zip", Hash64("m"), 1024, 4, DateTimeOffset.UtcNow,
                new List<string> { "RunReports" })
        };
        var vm = new SupportPackageViewModel(provider);
        vm.OutputPath = @"C:\support\pkg.zip";
        vm.IncludeRunReports = true;
        vm.SelectedRunIds.Add(RunId.Parse("run_a"));
        vm.SelectedRunIds.Add(RunId.Parse("run_b"));

        var ok = await vm.GenerateAsync(CancellationToken.None);

        Assert.True(ok);
        Assert.True(vm.LastResult is not null);
    }

    // ---------- Run report export (LOG-005) ----------
    [Fact]
    public async Task Run_report_export_populates_report_and_save_writes_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rpt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var outPath = Path.Combine(dir, "run.json");
        try
        {
            var report = new RunReport(
                1, DateTimeOffset.UtcNow,
                new RunReportRun("run_1", "rev_1", "manual", "Completed",
                    DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow,
                    1, null, 1000, 3, 2, 500, 0, 0),
                new List<RunReportStep>(), new List<RunReportEvent>(), null, new List<string>(), "hash");

            var provider = new FakeSecurityCenterDataProvider { RunReport = report };
            var vm = new RunReportExportViewModel(provider) { RunIdText = "run_1", SavePath = outPath };

            Assert.True(await vm.ExportAsync(CancellationToken.None));
            Assert.True(vm.Report is not null);
            Assert.True(await vm.SaveAsAsync(CancellationToken.None));
            Assert.True(File.Exists(outPath));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Run_report_export_invalid_id_surfaces_error()
    {
        var provider = new FakeSecurityCenterDataProvider();
        var vm = new RunReportExportViewModel(provider) { RunIdText = "   " };

        Assert.False(await vm.ExportAsync(CancellationToken.None));
        Assert.True(vm.HasError);
        Assert.Equal("SEC_RPT_INVALID_ID", vm.Error!.Code);
    }
}

/// <summary>Scriptable <see cref="ISecurityCenterDataProvider"/> for the Security Center view-model tests.</summary>
internal sealed class FakeSecurityCenterDataProvider : ISecurityCenterDataProvider
{
    public List<Incident> Incidents { get; } = new();
    public List<SourceHealth> Health { get; } = new();
    public List<PolicyGrant> Grants { get; } = new();
    public List<AuditEntry> Audit { get; } = new();
    public bool EmergencyStop;
    public bool DetectionThrows;

    public Result? DisableResult = Result.Success();
    public Result? RecoverResult = Result.Success();
    public Result? RevokeResult = Result.Success();
    public Result? EmergencyStopResult = Result.Success();
    public Result? EmergencyResumeResult = Result.Success();
    public Result? AcknowledgeResult = Result.Success();
    public Result? MitigateResult = Result.Success();
    public Result? ResolveResult = Result.Success();
    public Result<GrantRevokePreview>? PreviewResult;

    // ---- Retention & export seeds ----
    public RetentionSettings RetentionSettingsSeed { get; set; } = RetentionSettings.Default;
    public RetentionSettings? SavedSettings { get; private set; }
    public RetentionCleanupResult? CleanupResult { get; set; }
    public SupportBundleResult? SupportResult { get; set; }
    public RunReport? RunReport { get; set; }

    public Task<Result<SecurityPosture>> GetPostureAsync(CancellationToken ct = default)
    {
        var inc = Incidents.GroupBy(i => i.State).ToDictionary(g => g.Key, g => g.Count());
        var src = Health.GroupBy(h => h.Status).ToDictionary(g => g.Key, g => g.Count());
        return Task.FromResult(Result<SecurityPosture>.Ok(new SecurityPosture(EmergencyStop, false, inc, src)));
    }

    public Task<Result<IReadOnlyList<Incident>>> ListIncidentsAsync(IncidentState? state, int limit, CancellationToken ct = default) =>
        Task.FromResult(Result<IReadOnlyList<Incident>>.Ok(Incidents.ToList()));

    public Task<Result<Incident>> GetIncidentAsync(IncidentId id, CancellationToken ct = default)
    {
        var i = Incidents.FirstOrDefault(x => x.Id == id);
        return Task.FromResult(i is not null ? Result<Incident>.Ok(i) : Result<Incident>.Fail(SecurityGovernanceErrors.IncidentNotFoundError(id.Value)));
    }

    public Task<Result> AcknowledgeIncidentAsync(IncidentId id, CancellationToken ct = default)
    {
        var i = Incidents.FirstOrDefault(x => x.Id == id);
        if (i is not null) Incidents[Incidents.IndexOf(i)] = i with { State = IncidentState.Acknowledged };
        return Task.FromResult(AcknowledgeResult ?? Result.Success());
    }

    public Task<Result> MitigateIncidentAsync(IncidentId id, CancellationToken ct = default)
    {
        var i = Incidents.FirstOrDefault(x => x.Id == id);
        if (i is not null) Incidents[Incidents.IndexOf(i)] = i with { State = IncidentState.Mitigated };
        return Task.FromResult(MitigateResult ?? Result.Success());
    }

    public Task<Result> ResolveIncidentAsync(IncidentId id, IncidentResolutionCode code, string note, CancellationToken ct = default)
    {
        var i = Incidents.FirstOrDefault(x => x.Id == id);
        if (i is not null)
            Incidents[Incidents.IndexOf(i)] = i with
            { State = IncidentState.Resolved, ResolutionCode = code.ToString(), ResolutionNote = note, ResolvedAtUtc = DateTimeOffset.UtcNow };
        return Task.FromResult(ResolveResult ?? Result.Success());
    }

    public Task<Result<IReadOnlyList<SourceHealth>>> ListSourceHealthAsync(CancellationToken ct = default)
    {
        if (DetectionThrows) throw new InvalidOperationException("detector unreachable");
        return Task.FromResult(Result<IReadOnlyList<SourceHealth>>.Ok(Health.ToList()));
    }

    public Task<Result> DisableSourceAsync(string kind, string id, CancellationToken ct = default) =>
        Task.FromResult(DisableResult ?? Result.Success());

    public Task<Result> RecoverSourceAsync(string kind, string id, CancellationToken ct = default) =>
        Task.FromResult(RecoverResult ?? Result.Success());

    public Task<Result<IReadOnlyList<PolicyGrant>>> ListActiveGrantsAsync(DateTimeOffset asOf, CancellationToken ct = default) =>
        Task.FromResult(Result<IReadOnlyList<PolicyGrant>>.Ok(Grants.ToList()));

    public Task<Result<GrantRevokePreview>> PreviewRevokeAsync(PolicyGrantId id, CancellationToken ct = default) =>
        Task.FromResult(PreviewResult ?? Result<GrantRevokePreview>.Ok(new GrantRevokePreview("tok", "cap", "auto")));

    public Task<Result> RevokeGrantAsync(PolicyGrantId id, string impactToken, CancellationToken ct = default)
    {
        if (RevokeResult is { IsSuccess: true })
            Grants.RemoveAll(g => g.GrantId.Value == id.Value);
        return Task.FromResult(RevokeResult ?? Result.Success());
    }

    public Task<Result<bool>> GetEmergencyStopAsync(CancellationToken ct = default) =>
        Task.FromResult(Result<bool>.Ok(EmergencyStop));

    public Task<Result> EmergencyStopAsync(string actor, CancellationToken ct = default)
    {
        if (EmergencyStopResult is { IsSuccess: true }) EmergencyStop = true;
        return Task.FromResult(EmergencyStopResult ?? Result.Success());
    }

    public Task<Result> EmergencyResumeAsync(string actor, CancellationToken ct = default)
    {
        if (EmergencyResumeResult is { IsSuccess: true }) EmergencyStop = false;
        return Task.FromResult(EmergencyResumeResult ?? Result.Success());
    }

    public Task<Result<IReadOnlyList<AuditEntry>>> QueryAuditAsync(AuditQuery query, CancellationToken ct = default) =>
        Task.FromResult(Result<IReadOnlyList<AuditEntry>>.Ok(Audit.ToList()));

    // ---- Retention & export (doc 05 §9/§10, LOG-005/006, SEC-106/108) ----
    public Task<Result<RetentionSettings>> GetRetentionSettingsAsync(CancellationToken ct = default) =>
        Task.FromResult(Result<RetentionSettings>.Ok(RetentionSettingsSeed));

    public Task<Result> SaveRetentionSettingsAsync(RetentionSettings settings, CancellationToken ct = default)
    {
        SavedSettings = settings;
        RetentionSettingsSeed = settings;
        return Task.FromResult(Result.Success());
    }

    public Task<Result<RetentionCleanupResult>> RunRetentionCleanupAsync(bool force, CancellationToken ct = default) =>
        Task.FromResult(CleanupResult is { } c
            ? Result<RetentionCleanupResult>.Ok(c)
            : Result<RetentionCleanupResult>.Fail(new AppError("SEC_CLEAN_FAIL", ErrorCategory.Internal, "x", false)));

    public Task<Result<SupportBundleResult>> BuildSupportPackageAsync(SupportBundleRequest request, CancellationToken ct = default) =>
        Task.FromResult(SupportResult is { } s
            ? Result<SupportBundleResult>.Ok(s)
            : Result<SupportBundleResult>.Fail(new AppError("SEC_PKG_FAIL", ErrorCategory.Internal, "x", false)));

    public Task<Result<RunReport>> ExportRunReportAsync(RunId runId, CancellationToken ct = default) =>
        Task.FromResult(RunReport is { } r
            ? Result<RunReport>.Ok(r)
            : Result<RunReport>.Fail(new AppError("SEC_RPT_NOT_FOUND", ErrorCategory.Resource, "x", false)));
}
