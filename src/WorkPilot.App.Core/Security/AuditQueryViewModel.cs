using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.App.Core.Primitives;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Domain.Security.Audit;

namespace WorkPilot.App.Core.Security;

/// <summary>
/// Audit tab (doc 06 §8). Queries the tamper-evident audit log with category/action/actor/time filters
/// and surfaces each entry's decision trace. Entries carry only safe, display-name-free data and the
/// DecisionTrace JSON (never the policy store's secret-bearing JSON). When a query returns nothing the
/// UI shows an empty state, never a sample list (doc 06 §10).
/// </summary>
public sealed class AuditQueryViewModel : ObservableBase
{
    private readonly ISecurityCenterDataProvider _provider;

    private bool _isLoading;
    private AppError? _error;
    private AuditCategory? _category;
    private string? _action;
    private string? _actor;
    private DateTimeOffset? _fromUtc;
    private DateTimeOffset? _toUtc;
    private int _limit = 200;
    private AuditEntry? _selected;
    private string? _decisionTrace;

    private readonly ObservableCollection<AuditEntry> _entries = new();

    public AuditQueryViewModel(ISecurityCenterDataProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        RunQueryCommand = new AsyncRelayCommand((_, ct) => RunQueryAsync(ct));
    }

    public ObservableCollection<AuditEntry> Entries => _entries;

    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }
    public AppError? Error { get => _error; private set { if (Set(ref _error, value)) Raise(nameof(HasError)); } }
    public bool HasError => _error is not null;
    public bool IsEmpty => !_isLoading && _error is null && _entries.Count == 0;

    public AuditCategory? CategoryFilter { get => _category; set => Set(ref _category, value); }
    public string? ActionFilter { get => _action; set => Set(ref _action, value); }
    public string? ActorFilter { get => _actor; set => Set(ref _actor, value); }
    public DateTimeOffset? FromUtcFilter { get => _fromUtc; set => Set(ref _fromUtc, value); }
    public DateTimeOffset? ToUtcFilter { get => _toUtc; set => Set(ref _toUtc, value); }
    public int Limit { get => _limit; set => Set(ref _limit, value); }

    public AuditEntry? SelectedEntry { get => _selected; private set => Set(ref _selected, value); }
    /// <summary>The decision trace of the selected entry, rendered as-is (safe, secret-free JSON).</summary>
    public string? DecisionTrace { get => _decisionTrace; private set => Set(ref _decisionTrace, value); }

    public AsyncRelayCommand RunQueryCommand { get; }

    public async Task RunQueryAsync(CancellationToken ct = default)
    {
        IsLoading = true; Error = null;
        try
        {
            var query = new AuditQuery(_category, _action, _actor, _fromUtc, _toUtc, _limit);
            var res = await _provider.QueryAuditAsync(query, ct);
            if (!res.IsSuccess) { Error = res.Error; return; }
            _entries.Clear();
            foreach (var e in res.Value!) _entries.Add(e); // never synthesize rows (doc 06 §10)
        }
        finally { IsLoading = false; Raise(nameof(IsEmpty)); }
    }

    public void SelectEntry(AuditEntry entry)
    {
        SelectedEntry = entry;
        DecisionTrace = entry?.DecisionTraceJson;
    }
}
