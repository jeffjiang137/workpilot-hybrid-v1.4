using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.App.Core.Primitives;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Domain.Security;
using WorkPilot.Application.Security.Governance;

namespace WorkPilot.App.Core.Security;

/// <summary>
/// Sources tab (doc 06 §6.2 / §7). Lists connector/MCP source health and issues disable / recover
/// commands. It explicitly models the two doc 06 §10 failure modes:
/// <list type="bullet">
///   <item><description><c>DetectionDegraded</c> — when the health probe is unreachable the load
///   surfaces the failure and flags the data incomplete; it never presents 0 sources as "safe" and
///   never swallows the exception.</description></item>
///   <item><description><c>PartialFailure</c> — when a disable's backend sub-actions partly fail the
///   command result carries which sub-actions succeeded vs failed so the UI can show them and offer a
///   safe retry.</description></item>
/// </list>
/// </summary>
public sealed class SourceHealthViewModel : ObservableBase
{
    private const int DefaultLimit = 500;

    private readonly ISecurityCenterDataProvider _provider;

    private bool _isLoading;
    private AppError? _error;
    private bool _detectionDegraded;
    private bool _healthDataIncomplete;
    private bool _partialFailure;
    private List<string> _failedSubActions = new();
    private List<string> _succeededSubActions = new();
    private string? _lastDisableDetail;

    private readonly ObservableCollection<SourceHealth> _health = new();

    public SourceHealthViewModel(ISecurityCenterDataProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        RefreshCommand = new AsyncRelayCommand((_, ct) => LoadAsync(ct));
        DisableCommand = new AsyncRelayCommand((p, ct) => DisableAsync((SourceRef)p!, ct));
        RecoverCommand = new AsyncRelayCommand((p, ct) => RecoverAsync((SourceRef)p!, ct));
    }

    public ObservableCollection<SourceHealth> Health => _health;

    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }
    public AppError? Error { get => _error; private set { if (Set(ref _error, value)) Raise(nameof(HasError)); } }
    public bool HasError => _error is not null;
    public bool IsEmpty => !_isLoading && _error is null && !_detectionDegraded && _health.Count == 0;

    /// <summary>True when the detector/health probe could not be reached (doc 06 §10 Detection degraded).</summary>
    public bool DetectionDegraded { get => _detectionDegraded; private set => Set(ref _detectionDegraded, value); }
    /// <summary>True when the health list is incomplete because detection is degraded — 0 must not read as safe.</summary>
    public bool HealthDataIncomplete { get => _healthDataIncomplete; private set => Set(ref _healthDataIncomplete, value); }

    /// <summary>True when a disable command partly failed (doc 06 §10 partial failure).</summary>
    public bool PartialFailure { get => _partialFailure; private set => Set(ref _partialFailure, value); }
    public IReadOnlyList<string> FailedSubActions => _failedSubActions;
    public IReadOnlyList<string> SucceededSubActions => _succeededSubActions;
    public string? LastDisableDetail { get => _lastDisableDetail; private set => Set(ref _lastDisableDetail, value); }

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand DisableCommand { get; }
    public AsyncRelayCommand RecoverCommand { get; }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        IsLoading = true; Error = null;
        DetectionDegraded = false; HealthDataIncomplete = false;
        try
        {
            Result<IReadOnlyList<SourceHealth>> res;
            try
            {
                res = await _provider.ListSourceHealthAsync(ct);
            }
            catch (Exception ex)
            {
                // Doc 06 §10: detector fault must be surfaced, NOT swallowed.
                DetectionDegraded = true;
                HealthDataIncomplete = true;
                Error = new AppError("SEC_DETECTION_DEGRADED", ErrorCategory.Internal,
                    "SecurityCenter.DetectionDegraded", false,
                    new Dictionary<string, string> { ["detail"] = ex.Message });
                return;
            }
            if (!res.IsSuccess)
            {
                DetectionDegraded = true;
                HealthDataIncomplete = true;
                Error = res.Error;
                return;
            }
            _health.Clear();
            foreach (var h in res.Value!) _health.Add(h);
        }
        finally { IsLoading = false; Raise(nameof(IsEmpty)); }
    }

    public async Task<bool> DisableAsync(SourceRef target, CancellationToken ct = default)
    {
        PartialFailure = false; _failedSubActions = new(); _succeededSubActions = new(); LastDisableDetail = null;
        var res = await _provider.DisableSourceAsync(target.Kind, target.Id, ct);
        if (res.IsSuccess)
        {
            _succeededSubActions = new List<string> { "disable", "terminate" };
            await LoadAsync(ct);
            return true;
        }
        Error = res.Error;
        if (res.Error?.Code == SecurityGovernanceErrors.PartialFailure.Code)
        {
            PartialFailure = true;
            ParsePartialFailure(res.Error!);
        }
        return false;
    }

    public async Task<bool> RecoverAsync(SourceRef target, CancellationToken ct = default)
    {
        PartialFailure = false; _failedSubActions = new(); _succeededSubActions = new(); LastDisableDetail = null;
        var res = await _provider.RecoverSourceAsync(target.Kind, target.Id, ct);
        if (!res.IsSuccess) { Error = res.Error; return false; }
        await LoadAsync(ct);
        return true;
    }

    /// <summary>
    /// Parses the <c>summary</c> field of <see cref="SecurityGovernanceErrors.PartialFailure"/> (format
    /// <c>"disable:CODE; terminate:CODE"</c>) into succeeded/failed sub-action lists so the UI can show
    /// exactly what worked and what did not (doc 06 §10).
    /// </summary>
    private void ParsePartialFailure(AppError error)
    {
        var known = new[] { "disable", "terminate" };
        var failed = new List<string>();
        if (error.SafeDetails.TryGetValue("summary", out var summary) && summary is not null)
        {
            LastDisableDetail = summary;
            foreach (var part in summary.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split(':', 2);
                if (kv.Length == 2) failed.Add($"{kv[0].Trim()}:{kv[1].Trim()}");
            }
        }
        _failedSubActions = failed;
        _succeededSubActions = known.Where(k => !failed.Any(f => f.StartsWith(k + ":", StringComparison.Ordinal)))
            .ToList();
    }
}

/// <summary>Lightweight (kind, id) reference for a source so commands don't depend on the full health record.</summary>
public sealed record SourceRef(string Kind, string Id);
