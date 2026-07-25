using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.App.Core.Primitives;
using WorkPilot.Application.Automation.Run;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation.Run;

namespace WorkPilot.App.Core.Runs;

/// <summary>
/// Run detail view model (LOG-002). Projects a hydrated run into a timeline + status transitions,
/// supports live updates from an <see cref="IRunFeed"/>, and recovers from a disconnect gap by
/// re-fetching the full event sequence (<c>RefillGapAsync</c>) so the UI never shows a hole. No WinUI,
/// Repository, Connector, Secret, or Native dependency (AI dev rule §3).
/// </summary>
public sealed class RunDetailViewModel : ObservableBase, IDisposable
{
    private readonly IRunRepository _runs;
    private IDisposable? _feedSub;
    private RunId? _runId;
    private RunDetailView? _detail;
    private bool _isLoading;
    private bool _isLiveGap;
    private AppError? _error;
    private IReadOnlyList<RunEvent> _events = Array.Empty<RunEvent>();

    public RunDetailViewModel(IRunRepository runs)
    {
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        RefreshCommand = new AsyncRelayCommand((_, _) => _runId is not null ? LoadAsync(_runId.Value) : Task.CompletedTask);
        RerunCommand = new AsyncRelayCommand((_, ct) => RerunAsync(ct), _ => _runId is not null);
        CancelCommand = new AsyncRelayCommand((_, ct) => CancelAsync(ct), _ => _runId is not null);
    }

    public RunDetailView? Detail { get => _detail; private set { if (Set(ref _detail, value)) NotifyDerived(); } }
    public bool IsLoading { get => _isLoading; private set { if (Set(ref _isLoading, value)) NotifyDerived(); } }
    public bool IsLiveGap { get => _isLiveGap; private set => Set(ref _isLiveGap, value); }
    public AppError? Error { get => _error; private set { if (Set(ref _error, value)) NotifyDerived(); } }
    public bool HasError => _error is not null;

    /// <summary>Safe I/O summary derived from the run's snapshots (LOG-004). No body or secret.</summary>
    public SafeSummary SafeSummary
    {
        get
        {
            if (_detail is null || _lastDetails is null) return new SafeSummary(Array.Empty<SafeFieldSummary>(), Array.Empty<SafeFieldSummary>(), 0, 0);
            return SafeSummaryProjector.Project(_lastDetails.Snapshot.CapabilitySnapshotJson, FirstStepOutputJson());
        }
    }

    private RunWithDetails? _lastDetails;

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand RerunCommand { get; }
    public AsyncRelayCommand CancelCommand { get; }

    /// <summary>Attaches the view model to a live feed filtered by <paramref name="runId"/>.</summary>
    public void AttachFeed(IRunFeed feed, RunId runId)
    {
        _runId = runId;
        _feedSub?.Dispose();
        _feedSub = feed?.Subscribe(item =>
        {
            if (item.RunId == runId)
                PushLiveEvents(item.Events);
        });
    }

    /// <summary>Loads the full run detail.</summary>
    public async Task LoadAsync(RunId runId, CancellationToken ct = default)
    {
        _runId = runId;
        IsLoading = true;
        IsLiveGap = false;
        Error = null;
        try
        {
            var r = await _runs.GetRunAsync(runId, ct);
            if (!r.IsSuccess) { Error = r.Error; return; }
            ApplyDetails(r.Value!);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Merges live events by sequence. If a gap is detected (incoming min &gt; last+1) the view
    /// marks <see cref="IsLiveGap"/> and waits for the caller to call <see cref="RefillGapAsync"/>.</summary>
    public void PushLiveEvents(IReadOnlyList<RunEvent> incoming)
    {
        if (incoming is null || incoming.Count == 0) return;
        var ordered = incoming.OrderBy(e => e.Sequence).ToList();
        var lastSeq = _events.Count == 0 ? 0 : _events.Max(e => e.Sequence);
        if (ordered[0].Sequence > lastSeq + 1)
        {
            IsLiveGap = true; // disconnect gap — caller must RefillGapAsync
            return;
        }
        MergeEvents(ordered);
    }

    /// <summary>Re-fetches the full event sequence to close a live gap (LOG-002 reconnect).</summary>
    public async Task RefillGapAsync(CancellationToken ct = default)
    {
        if (_runId is null) return;
        var r = await _runs.GetRunAsync(_runId.Value, ct);
        if (!r.IsSuccess) { Error = r.Error; return; }
        ApplyDetails(r.Value!);
        IsLiveGap = false;
    }

    private void ApplyDetails(RunWithDetails d)
    {
        _lastDetails = d;
        _events = d.Events.OrderBy(e => e.Sequence).ToList();
        Detail = Project(d);
    }

    private void MergeEvents(IReadOnlyList<RunEvent> newOnes)
    {
        var bySeq = _events.ToDictionary(e => e.Sequence);
        foreach (var e in newOnes)
            bySeq[e.Sequence] = e;
        _events = bySeq.Values.OrderBy(e => e.Sequence).ToList();
        if (_lastDetails is not null)
            Detail = Detail! with { EventCount = _events.Count, Transitions = BuildTransitions(_events) };
    }

    private string? FirstStepOutputJson()
    {
        if (_lastDetails is null) return null;
        var first = _lastDetails.Steps.OrderBy(s => s.StartedAtUtc).FirstOrDefault();
        return first?.OutputSummaryJson;
    }

    private RunDetailView Project(RunWithDetails d)
    {
        var steps = d.Steps
            .OrderBy(s => s.StartedAtUtc)
            .Select(s => new RunStepView(s.Id, s.NodeId, s.NodeKind, s.Status, s.StartedAtUtc,
                s.FinishedAtUtc, s.DurationMs, s.ErrorCode))
            .ToList();
        var transitions = BuildTransitions(d.Events.OrderBy(e => e.Sequence).ToList());
        return new RunDetailView(d.Run.Id, d.Run.AutomationId, d.Run.Status, d.Run.Priority,
            d.Run.ScheduledAtUtc, d.Run.StartedAtUtc, d.Run.FinishedAtUtc, d.Run.ParentRunId,
            d.Run.FinalErrorCode, steps, transitions, d.Events.Count);
    }

    /// <summary>Builds status transitions from events that carry a <c>to_status</c> property (LOG-002 timeline).</summary>
    private static IReadOnlyList<StatusTransition> BuildTransitions(IReadOnlyList<RunEvent> events)
    {
        var result = new List<StatusTransition>();
        foreach (var e in events)
        {
            if (string.IsNullOrWhiteSpace(e.SafePropertiesJson)) continue;
            try
            {
                using var doc = JsonDocument.Parse(e.SafePropertiesJson);
                if (doc.RootElement.TryGetProperty("to_status", out var el) && el.ValueKind == JsonValueKind.String)
                {
                    if (Enum.TryParse<RunStatus>(el.GetString(), ignoreCase: true, out _))
                    {
                        // map storage string → enum via the domain storage map
                        var status = RunStorageMaps.StatusFromStorage(el.GetString()!);
                        result.Add(new StatusTransition(e.OccurredAtUtc, status, e.Code));
                    }
                }
            }
            catch (JsonException) { /* ignore malformed event props */ }
        }
        return result;
    }

    private void NotifyDerived()
    {
        Raise(nameof(HasError));
        Raise(nameof(SafeSummary));
    }

    private async Task RerunAsync(CancellationToken ct)
    {
        if (_runId is null) return;
        // Rerun is orchestrated by RerunOrchestrator; this command reloads the new run's detail via the feed
        // once the host creates it. The orchestrator itself is invoked by the host/App shell. Here we simply
        // re-fetch so the UI reflects the (pending) rerun state. (RUN-006 surfaced to UI.)
        await LoadAsync(_runId.Value, ct);
    }

    private async Task CancelAsync(CancellationToken ct)
    {
        if (_runId is null) return;
        var r = await _runs.RequestCancellationAsync(_runId.Value, DateTimeOffset.UtcNow, ct);
        if (!r.IsSuccess) { Error = r.Error; return; }
        await LoadAsync(_runId.Value, ct);
    }

    public void Dispose() => _feedSub?.Dispose();
}
