using System;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Application.Security.Retention;
using WorkPilot.App.Core.Primitives;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Domain.Security.Retention;

namespace WorkPilot.App.Core.Security;

/// <summary>
/// Operations tab — retention policy + cleanup (doc 05 §9, SEC-106). Two-way binds the retention
/// windows (run / event / audit), loads and saves them through <see cref="ISecurityCenterDataProvider"/>,
/// and exposes a manual "clean now" action. All clamping happens in the service layer; the view model
/// reloads after a save so the UI always reflects the persisted (clamped) values.
/// </summary>
public sealed class RetentionSettingsViewModel : ObservableBase
{
    private readonly ISecurityCenterDataProvider _provider;
    private int _runDays;
    private int _eventDays;
    private int _auditDays;
    private DateTimeOffset? _lastCleanupAtUtc;
    private bool _lastCleanupSkipped;
    private bool _isBusy;
    private AppError? _error;
    private RetentionCleanupResult? _lastCleanupResult;

    public RetentionSettingsViewModel(ISecurityCenterDataProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        LoadCommand = new AsyncRelayCommand((_, ct) => LoadAsync(ct));
        SaveCommand = new AsyncRelayCommand((_, ct) => SaveAsync(ct), _ => !_isBusy);
        CleanupNowCommand = new AsyncRelayCommand((_, ct) => CleanupNowAsync(ct), _ => !_isBusy);
    }

    public int RunDays { get => _runDays; set => Set(ref _runDays, value); }
    public int EventDays { get => _eventDays; set => Set(ref _eventDays, value); }
    public int AuditDays { get => _auditDays; set => Set(ref _auditDays, value); }

    public DateTimeOffset? LastCleanupAtUtc { get => _lastCleanupAtUtc; private set => Set(ref _lastCleanupAtUtc, value); }
    public bool LastCleanupSkipped { get => _lastCleanupSkipped; private set => Set(ref _lastCleanupSkipped, value); }
    public RetentionCleanupResult? LastCleanupResult { get => _lastCleanupResult; private set => Set(ref _lastCleanupResult, value); }

    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
    public AppError? Error { get => _error; private set { if (Set(ref _error, value)) Raise(nameof(HasError)); } }
    public bool HasError => _error is not null;

    public AsyncRelayCommand LoadCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand CleanupNowCommand { get; }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        IsBusy = true; Error = null;
        try
        {
            var res = await _provider.GetRetentionSettingsAsync(ct);
            if (!res.IsSuccess) { Error = res.Error!; return; }
            var s = res.Value!;
            RunDays = s.Policy.RunDays;
            EventDays = s.Policy.EventDays;
            AuditDays = s.Policy.AuditDays;
            LastCleanupAtUtc = s.LastCleanupAtUtc;
            LastCleanupSkipped = false;
            LastCleanupResult = null;
        }
        finally { IsBusy = false; }
    }

    public async Task<bool> SaveAsync(CancellationToken ct = default)
    {
        IsBusy = true; Error = null;
        try
        {
            var policy = new RetentionPolicy(RunDays, EventDays, AuditDays).Clamp();
            var settings = new RetentionSettings(policy, null);
            var res = await _provider.SaveRetentionSettingsAsync(settings, ct);
            if (!res.IsSuccess) { Error = res.Error!; return false; }
            await LoadAsync(ct); // reflect the clamped + persisted values
            return true;
        }
        finally { IsBusy = false; }
    }

    public async Task<bool> CleanupNowAsync(CancellationToken ct = default)
    {
        IsBusy = true; Error = null; LastCleanupResult = null;
        try
        {
            var res = await _provider.RunRetentionCleanupAsync(force: true, ct);
            if (!res.IsSuccess) { Error = res.Error!; return false; }
            LastCleanupResult = res.Value!;
            LastCleanupSkipped = res.Value!.SkippedBecauseAlreadyRunToday;
            LastCleanupAtUtc = res.Value!.CompletedAtUtc ?? LastCleanupAtUtc;
            return true;
        }
        finally { IsBusy = false; }
    }
}
