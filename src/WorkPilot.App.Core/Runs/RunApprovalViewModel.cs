using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.App.Core.Primitives;
using WorkPilot.Application.Automation.Run;
using WorkPilot.Application.Automation.Run.Approval;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation.Run;

namespace WorkPilot.App.Core.Runs;

/// <summary>
/// Approval inbox view model (RUN-007). Lists runs awaiting High one-time approval, surfaces a
/// time-boxed <see cref="ApprovalPrompt"/> (10-minute window), and approves via
/// <see cref="ApprovalCoordinator"/> (race guard + precondition re-check + single-use receipt).
/// Dismissing/closing a prompt (UI-A06, Esc) makes <em>no</em> decision: it is removed from the local
/// list but the coordinator is never called, so the run stays WaitingApproval on the server.
/// </summary>
public sealed class RunApprovalViewModel : ObservableBase
{
    private readonly IRunRepository _runs;
    private readonly ApprovalCoordinator _coordinator;
    private readonly IClock _clock;

    private bool _isLoading;
    private AppError? _error;
    private readonly ObservableCollection<ApprovalPrompt> _prompts = new();
    private readonly Dictionary<RunId, AutomationRun> _runsById = new();

    public RunApprovalViewModel(IRunRepository runs, ApprovalCoordinator coordinator, IClock clock)
    {
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        RefreshCommand = new AsyncRelayCommand((_, _) => LoadPendingAsync());
    }

    public ObservableCollection<ApprovalPrompt> Prompts => _prompts;
    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }
    public AppError? Error { get => _error; private set { if (Set(ref _error, value)) Raise(nameof(HasError)); } }
    public bool HasError => _error is not null;
    public bool HasPrompts => _prompts.Count > 0;

    public AsyncRelayCommand RefreshCommand { get; }

    /// <summary>Loads all runs currently WaitingApproval and builds a prompt for each.</summary>
    public async Task LoadPendingAsync(CancellationToken ct = default)
    {
        IsLoading = true;
        Error = null;
        _prompts.Clear();
        _runsById.Clear();
        try
        {
            var page = await _runs.ListRunsAsync(new RunQuery(Status: RunStatus.WaitingApproval, PageSize: 200), ct);
            if (!page.IsSuccess) { Error = page.Error; return; }

            foreach (var item in page.Value!.Items)
            {
                var detail = await _runs.GetRunAsync(item.Id, ct);
                if (!detail.IsSuccess || detail.Value is null) continue;
                var d = detail.Value!;
                var (approvalId, stepId) = ExtractApproval(d);
                if (approvalId is null) continue;

                _runsById[item.Id] = d.Run;
                _prompts.Add(new ApprovalPrompt(
                    item.Id, approvalId, stepId,
                    SafeSummaryJsonFor(d),
                    RiskLevelFor(d),
                    _clock.UtcNow,                 // prompt created "now" for display
                    _clock.UtcNow.AddMinutes(Limits.V1_5.ApprovalDecisionWindowMinutes)));
            }
        }
        finally
        {
            IsLoading = false;
            Raise(nameof(HasPrompts));
        }
    }

    /// <summary>Approves a pending request. On success the prompt leaves the inbox.</summary>
    public async Task<Result<ApprovalDecisionOutcome>> ApproveAsync(ApprovalPrompt prompt, ApprovalDecisionContext ctx, CancellationToken ct = default)
    {
        var r = await _coordinator.ApproveAsync(prompt.ApprovalId, ctx, ct);
        if (r.IsSuccess)
        {
            RemovePrompt(prompt);
            Error = null;
        }
        else
        {
            Error = r.Error;
        }
        return r;
    }

    /// <summary>UI-A06: closing/dismissing a prompt makes no decision. The coordinator is never called.</summary>
    public void DismissAsync(ApprovalPrompt prompt)
    {
        RemovePrompt(prompt);
        Error = null;
    }

    private void RemovePrompt(ApprovalPrompt prompt)
    {
        for (var i = 0; i < _prompts.Count; i++)
        {
            if (_prompts[i].ApprovalId == prompt.ApprovalId)
            {
                _prompts.RemoveAt(i);
                break;
            }
        }
        Raise(nameof(HasPrompts));
    }

    private static (string? ApprovalId, StepRunId) ExtractApproval(RunWithDetails d)
    {
        foreach (var e in d.Events)
        {
            if (e.Code == RunEventCodes.ApprovalCreated)
            {
                string? approvalId = null;
                try
                {
                    using var doc = JsonDocument.Parse(e.SafePropertiesJson);
                    if (doc.RootElement.TryGetProperty("approval_id", out var el) && el.ValueKind == JsonValueKind.String)
                        approvalId = el.GetString();
                }
                catch (JsonException) { /* ignore */ }
                var stepId = e.StepId ?? default;
                return (approvalId, stepId);
            }
        }
        return (null, default);
    }

    private static string SafeSummaryJsonFor(RunWithDetails d) =>
        d.Snapshot.CapabilitySnapshotJson;

    private static int RiskLevelFor(RunWithDetails d) =>
        d.Run.Priority > 0 ? Math.Min(3, d.Run.Priority) : 0;
}
