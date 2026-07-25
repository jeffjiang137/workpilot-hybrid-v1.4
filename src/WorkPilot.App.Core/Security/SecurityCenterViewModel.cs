using System;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.App.Core.Primitives;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;

namespace WorkPilot.App.Core.Security;

/// <summary>
/// Top-level Security Center view model (doc 06 §1). Owns tab navigation, the shared emergency-stop
/// state, and the detection-degraded signal, and composes the six tab view models (Posture dashboard,
/// Incidents, Sources, Grants, Audit, Operations/support-package). All governance commands go through
/// <see cref="ISecurityCenterDataProvider"/>; this type holds no secret, connector, native or
/// repository dependency (AI dev rule §3).
/// </summary>
public sealed class SecurityCenterViewModel : ObservableBase
{
    private readonly ISecurityCenterDataProvider _provider;

    private SecurityCenterTab _selectedTab = SecurityCenterTab.Posture;
    private bool _emergencyStopActive;
    private bool _detectionDegraded;
    private bool _isBusy;
    private AppError? _error;
    private string _actor = "operator";

    public SecurityCenterViewModel(ISecurityCenterDataProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        Incidents = new IncidentListViewModel(provider);
        Sources = new SourceHealthViewModel(provider);
        Grants = new GrantListViewModel(provider);
        Audit = new AuditQueryViewModel(provider);
        Support = new SupportPackageViewModel(provider);
        Retention = new RetentionSettingsViewModel(provider);
        RunReport = new RunReportExportViewModel(provider);

        SelectTabCommand = new RelayCommand(p => SelectTab((SecurityCenterTab)p!));
        EmergencyStopCommand = new AsyncRelayCommand((_, ct) => StopAsync(_actor, ct));
        EmergencyResumeCommand = new AsyncRelayCommand((_, ct) => ResumeAsync(_actor, ct));
        LoadPostureCommand = new AsyncRelayCommand((_, ct) => LoadPostureAsync(ct));
    }

    public IncidentListViewModel Incidents { get; }
    public SourceHealthViewModel Sources { get; }
    public GrantListViewModel Grants { get; }
    public AuditQueryViewModel Audit { get; }
    public SupportPackageViewModel Support { get; }
    public RetentionSettingsViewModel Retention { get; }
    public RunReportExportViewModel RunReport { get; }

    public SecurityCenterTab SelectedTab
    {
        get => _selectedTab;
        private set { if (Set(ref _selectedTab, value)) Raise(nameof(SelectedTabTitle)); }
    }

    /// <summary>Human-readable title for the active tab (bound by the WinUI header).</summary>
    public string SelectedTabTitle => _selectedTab switch
    {
        SecurityCenterTab.Posture => "态势",
        SecurityCenterTab.Incidents => "事件",
        SecurityCenterTab.Sources => "来源生命周期",
        SecurityCenterTab.Grants => "权限配置",
        SecurityCenterTab.Audit => "审计查询",
        SecurityCenterTab.Operations => "保留 / 诊断 / 紧急停止",
        _ => "安全中心"
    };

    /// <summary>True when the global emergency stop is active (doc 06 §6.4).</summary>
    public bool EmergencyStopActive { get => _emergencyStopActive; private set => Set(ref _emergencyStopActive, value); }
    /// <summary>Surfaced from posture: detection subsystem is degraded (doc 06 §10).</summary>
    public bool DetectionDegraded { get => _detectionDegraded; private set => Set(ref _detectionDegraded, value); }
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
    public AppError? Error { get => _error; private set { if (Set(ref _error, value)) Raise(nameof(HasError)); } }
    public bool HasError => _error is not null;

    /// <summary>The actor recorded on governance commands (defaults to "operator").</summary>
    public string Actor { get => _actor; set => Set(ref _actor, value); }

    public RelayCommand SelectTabCommand { get; }
    public AsyncRelayCommand EmergencyStopCommand { get; }
    public AsyncRelayCommand EmergencyResumeCommand { get; }
    public AsyncRelayCommand LoadPostureCommand { get; }

    public void SelectTab(SecurityCenterTab tab)
    {
        SelectedTab = tab;
        _ = tab switch
        {
            SecurityCenterTab.Incidents => Incidents.LoadAsync(),
            SecurityCenterTab.Sources => Sources.LoadAsync(),
            SecurityCenterTab.Grants => Grants.LoadAsync(),
            SecurityCenterTab.Audit => Audit.RunQueryAsync(),
            SecurityCenterTab.Operations => Retention.LoadAsync(),
            _ => Task.CompletedTask
        };
    }

    /// <summary>Loads the posture snapshot and refreshes the shared emergency-stop / detection flags.</summary>
    public async Task LoadPostureAsync(CancellationToken ct = default)
    {
        IsBusy = true; Error = null;
        try
        {
            var res = await _provider.GetPostureAsync(ct);
            if (!res.IsSuccess) { Error = res.Error; return; }
            EmergencyStopActive = res.Value!.EmergencyStopActive;
            DetectionDegraded = res.Value!.DetectionDegraded;
        }
        finally { IsBusy = false; }
    }

    public async Task<bool> StopAsync(string actor, CancellationToken ct = default)
    {
        IsBusy = true; Error = null;
        try
        {
            var res = await _provider.EmergencyStopAsync(actor, ct);
            if (!res.IsSuccess) { Error = res.Error; await LoadPostureAsync(ct); return false; }
            EmergencyStopActive = true;
            await LoadPostureAsync(ct);
            return true;
        }
        finally { IsBusy = false; }
    }

    public async Task<bool> ResumeAsync(string actor, CancellationToken ct = default)
    {
        IsBusy = true; Error = null;
        try
        {
            var res = await _provider.EmergencyResumeAsync(actor, ct);
            if (!res.IsSuccess) { Error = res.Error; return false; }
            EmergencyStopActive = false;
            await LoadPostureAsync(ct);
            return true;
        }
        finally { IsBusy = false; }
    }
}
