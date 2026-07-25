using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.App.Core.Primitives;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Security;

namespace WorkPilot.App.Core.Security;

/// <summary>
/// Incidents tab (doc 06 §3). Lists aggregated incidents, supports drill-down to a single incident,
/// and issues the acknowledge / mitigate / resolve lifecycle commands. Charts bind to
/// <see cref="SeverityBreakdown"/>, which is computed only from real rows — when there are no rows it
/// is empty and the UI shows an empty state, never a sample curve (doc 06 §10). No secret / connector
/// / native dependency: it only projects port data and forwards commands to the facade.
/// </summary>
public sealed class IncidentListViewModel : ObservableBase
{
    private const int DefaultLimit = 200;

    private readonly ISecurityCenterDataProvider _provider;

    private bool _isLoading;
    private AppError? _error;
    private IncidentState? _stateFilter;
    private Incident? _selected;
    private bool _drillDownOpen;

    private readonly ObservableCollection<Incident> _items = new();

    public IncidentListViewModel(ISecurityCenterDataProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        RefreshCommand = new AsyncRelayCommand((_, ct) => LoadAsync(ct));
        AcknowledgeCommand = new AsyncRelayCommand((p, ct) => AcknowledgeAsync((IncidentId)p!, ct));
        MitigateCommand = new AsyncRelayCommand((p, ct) => MitigateAsync((IncidentId)p!, ct));
    }

    public ObservableCollection<Incident> Items => _items;

    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }
    public AppError? Error { get => _error; private set { if (Set(ref _error, value)) Raise(nameof(HasError)); } }
    public bool HasError => _error is not null;
    /// <summary>True only when a real load completed with zero rows and no error (doc 06 §10 empty state).</summary>
    public bool IsEmpty => !_isLoading && _error is null && _items.Count == 0;

    public IncidentState? StateFilter
    {
        get => _stateFilter;
        set { if (Set(ref _stateFilter, value)) _ = LoadAsync(); }
    }

    public Incident? SelectedIncident { get => _selected; private set => Set(ref _selected, value); }
    public bool DrillDownOpen { get => _drillDownOpen; private set => Set(ref _drillDownOpen, value); }

    /// <summary>Severity histogram for the chart. Always derived from <see cref="Items"/> — never synthetic.</summary>
    public IReadOnlyDictionary<SecuritySeverity, int> SeverityBreakdown =>
        _items.GroupBy(i => i.Severity).ToDictionary(g => g.Key, g => g.Count());

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand AcknowledgeCommand { get; }
    public AsyncRelayCommand MitigateCommand { get; }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        IsLoading = true; Error = null;
        try
        {
            var res = await _provider.ListIncidentsAsync(_stateFilter, DefaultLimit, ct);
            if (!res.IsSuccess) { Error = res.Error; return; }
            _items.Clear();
            foreach (var i in res.Value!) _items.Add(i); // no synthetic placeholder rows (doc 06 §10)
        }
        finally { IsLoading = false; Raise(nameof(IsEmpty)); Raise(nameof(SeverityBreakdown)); }
    }

    /// <summary>Drill into a single incident (doc 06 §1: down drill).</summary>
    public async Task<Incident?> OpenAsync(IncidentId id, CancellationToken ct = default)
    {
        var res = await _provider.GetIncidentAsync(id, ct);
        if (!res.IsSuccess) { Error = res.Error; return null; }
        SelectedIncident = res.Value!;
        DrillDownOpen = true;
        return res.Value!;
    }

    public void CloseDrillDown() { DrillDownOpen = false; SelectedIncident = null; }

    public async Task<bool> AcknowledgeAsync(IncidentId id, CancellationToken ct = default)
    {
        var res = await _provider.AcknowledgeIncidentAsync(id, ct);
        if (!res.IsSuccess) { Error = res.Error; return false; }
        await RefreshSelectedAsync(id, ct);
        return true;
    }

    public async Task<bool> MitigateAsync(IncidentId id, CancellationToken ct = default)
    {
        var res = await _provider.MitigateIncidentAsync(id, ct);
        if (!res.IsSuccess) { Error = res.Error; return false; }
        await RefreshSelectedAsync(id, ct);
        return true;
    }

    public async Task<bool> ResolveAsync(IncidentId id, IncidentResolutionCode code, string note, CancellationToken ct = default)
    {
        var res = await _provider.ResolveIncidentAsync(id, code, note, ct);
        if (!res.IsSuccess) { Error = res.Error; return false; }
        await RefreshSelectedAsync(id, ct);
        await LoadAsync(ct);
        return true;
    }

    private async Task RefreshSelectedAsync(IncidentId id, CancellationToken ct)
    {
        var res = await _provider.GetIncidentAsync(id, ct);
        if (res.IsSuccess) SelectedIncident = res.Value!;
    }
}
