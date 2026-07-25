using System;
using WorkPilot.App.Core.Primitives;
using WorkPilot.Domain.Schema;

namespace WorkPilot.App.Core.Maintenance;

/// <summary>
/// BCL view model that mirrors the current <see cref="MaintenanceState"/> into UI-bindable
/// properties. When the state is not <see cref="MaintenanceState.None"/> the app shows a banner and
/// must not enable or run automations (PKG-A06 / MIG-A06/A07).
/// </summary>
public sealed class MaintenanceBannerViewModel : ObservableBase
{
    private readonly IMaintenanceStateProvider _provider;
    private MaintenanceState _state;

    public MaintenanceBannerViewModel(IMaintenanceStateProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _state = provider.Current;
        provider.Changed += OnChanged;
    }

    public MaintenanceState State
    {
        get => _state;
        private set => Set(ref _state, value);
    }

    public bool IsVisible => _state != MaintenanceState.None;

    /// <summary>Localization key for the banner title.</summary>
    public string TitleKey => _state switch
    {
        MaintenanceState.UpgradeRequired => "MAINTENANCE_UPGRADE_REQUIRED",
        MaintenanceState.DatabaseTooNew => "MAINTENANCE_DATABASE_TOO_NEW",
        MaintenanceState.HostSchemaMismatch => "MAINTENANCE_HOST_SCHEMA_MISMATCH",
        MaintenanceState.ChecksumMismatch => "MAINTENANCE_CHECKSUM_MISMATCH",
        MaintenanceState.MaintenanceInProgress => "MAINTENANCE_IN_PROGRESS",
        _ => string.Empty
    };

    /// <summary>Localization key for the banner body message.</summary>
    public string MessageKey => _state switch
    {
        MaintenanceState.UpgradeRequired => "MAINTENANCE_UPGRADE_REQUIRED_BODY",
        MaintenanceState.DatabaseTooNew => "MAINTENANCE_DATABASE_TOO_NEW_BODY",
        MaintenanceState.HostSchemaMismatch => "MAINTENANCE_HOST_SCHEMA_MISMATCH_BODY",
        MaintenanceState.ChecksumMismatch => "MAINTENANCE_CHECKSUM_MISMATCH_BODY",
        MaintenanceState.MaintenanceInProgress => "MAINTENANCE_IN_PROGRESS_BODY",
        _ => string.Empty
    };

    /// <summary>UI severity: "error" blocks operation; "warning" advises.</summary>
    public string SeverityKey => _state is MaintenanceState.ChecksumMismatch or MaintenanceState.DatabaseTooNew
        ? "error"
        : "warning";

    private void OnChanged(object? sender, MaintenanceState state) => State = state;
}
