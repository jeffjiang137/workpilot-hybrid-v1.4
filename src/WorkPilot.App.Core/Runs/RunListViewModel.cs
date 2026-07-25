using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.App.Core.Primitives;
using WorkPilot.Application.Automation.Run;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation.Run;

namespace WorkPilot.App.Core.Runs;

/// <summary>
/// Run history list view model (LOG-001). Wraps <see cref="IRunRepository.ListRunsAsync"/> with
/// stable keyset pagination (<see cref="RunListCursor"/>), time/status/automation/trigger filters,
/// and the five UI states (loading / empty / content / no-result / error — UI-A01). No WinUI,
/// Repository, Connector, Secret, or Native dependency (AI dev rule §3): it only projects port data.
/// </summary>
public sealed class RunListViewModel : ObservableBase
{
    private const int DefaultPageSize = 50;

    private readonly IRunRepository _runs;

    private AutomationId? _automationId;
    private RunStatus? _status;
    private RunTriggerKind? _triggerKind;
    private DateTimeOffset? _fromUtc;
    private DateTimeOffset? _toUtc;

    private bool _isLoading;
    private bool _hasMore;
    private AppError? _error;
    private RunListCursor? _nextCursor;
    private readonly ObservableCollection<RunListItemView> _items = new();

    public RunListViewModel(IRunRepository runs)
    {
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        RefreshCommand = new AsyncRelayCommand((_, _) => LoadFirstPageAsync());
        NextPageCommand = new AsyncRelayCommand((_, _) => LoadNextPageAsync(), _ => _hasMore && !_isLoading);
    }

    /// <summary>Bound list of run rows. Mutated in place so data-binding stays stable.</summary>
    public ObservableCollection<RunListItemView> Items => _items;

    public bool IsLoading { get => _isLoading; private set { if (Set(ref _isLoading, value)) Raise(nameof(HasMore)); } }
    public bool HasMore { get => _hasMore && !_isLoading; private set => Set(ref _hasMore, value); }
    public AppError? Error { get => _error; private set { if (Set(ref _error, value)) NotifyStates(); } }
    public bool HasError => _error is not null;
    public bool IsEmpty => !_isLoading && _error is null && _items.Count == 0;
    /// <summary>A filter was applied but returned nothing (distinct from a never-populated list).</summary>
    public bool HasNoResult => !_isLoading && _error is null && _items.Count == 0 && AnyFilterSet;

    public AutomationId? AutomationIdFilter { get => _automationId; set { if (Set(ref _automationId, value)) Reload(); } }
    public RunStatus? StatusFilter { get => _status; set { if (Set(ref _status, value)) Reload(); } }
    public RunTriggerKind? TriggerKindFilter { get => _triggerKind; set { if (Set(ref _triggerKind, value)) Reload(); } }
    public DateTimeOffset? FromUtcFilter { get => _fromUtc; set { if (Set(ref _fromUtc, value)) Reload(); } }
    public DateTimeOffset? ToUtcFilter { get => _toUtc; set { if (Set(ref _toUtc, value)) Reload(); } }

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand NextPageCommand { get; }

    private bool AnyFilterSet =>
        _automationId is not null || _status is not null || _triggerKind is not null || _fromUtc is not null || _toUtc is not null;

    private void Reload()
    {
        _items.Clear();
        _nextCursor = null;
        _hasMore = false;
        _error = null;
        NotifyStates();
        _ = LoadFirstPageAsync();
    }

    private void NotifyStates()
    {
        Raise(nameof(IsEmpty));
        Raise(nameof(HasNoResult));
        Raise(nameof(HasError));
        Raise(nameof(IsLoading));
    }

    /// <summary>Loads the first page using the current filters (resets any prior pages).</summary>
    public async Task LoadFirstPageAsync(CancellationToken ct = default)
    {
        _items.Clear();
        _nextCursor = null;
        _hasMore = false;
        _error = null;
        IsLoading = true;
        NotifyStates();
        try
        {
            var page = await _runs.ListRunsAsync(BuildQuery(null), ct);
            if (!page.IsSuccess) { Error = page.Error; return; }
            ApplyPage(page.Value!);
        }
        finally
        {
            IsLoading = false;
            NotifyStates();
        }
    }

    /// <summary>Appends the next page if one is available.</summary>
    public async Task LoadNextPageAsync(CancellationToken ct = default)
    {
        if (!_hasMore || _nextCursor is null || _isLoading) return;
        IsLoading = true;
        NotifyStates();
        try
        {
            var page = await _runs.ListRunsAsync(BuildQuery(_nextCursor), ct);
            if (!page.IsSuccess) { Error = page.Error; return; }
            ApplyPage(page.Value!);
        }
        finally
        {
            IsLoading = false;
            NotifyStates();
        }
    }

    private void ApplyPage(RunListPage page)
    {
        foreach (var it in page.Items)
            _items.Add(ToView(it));
        _nextCursor = page.NextCursor;
        _hasMore = page.HasMore;
    }

    private RunQuery BuildQuery(RunListCursor? cursor) =>
        new(_automationId, _status, _triggerKind, _fromUtc, _toUtc, DefaultPageSize, cursor);

    private static RunListItemView ToView(RunListItem it) =>
        new(it.Id, it.AutomationId, it.TriggerKind, it.Status, it.Priority, it.ScheduledAtUtc,
            it.StartedAtUtc, it.FinishedAtUtc, it.FinalErrorCode);
}
