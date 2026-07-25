using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.App.Core.Primitives;
using WorkPilot.Application.Automation;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation;
using WorkPilot.Domain.Automation.Scheduling;

namespace WorkPilot.App.Core.Automation;

/// <summary>Which of the five editor steps is active (doc 02 §4).</summary>
public enum EditorStep
{
    BasicInfo = 0,
    Trigger = 1,
    Workflow = 2,
    Permissions = 3,
    TestEnable = 4
}

/// <summary>Whether the editor is creating a new automation or editing an existing one.</summary>
public enum EditorMode
{
    New,
    Edit
}

/// <summary>Lifecycle of the current save operation, surfaced to the UI for progress/cancel/error states.</summary>
public enum SaveState
{
    Idle,
    Saving,
    Saved,
    Error,
    Conflict
}

/// <summary>Outcome of a save attempt, consumed by tests and the UI conflict dialog.</summary>
public sealed record EditorSaveResult(bool Succeeded, bool Conflict, AppError? Error);

/// <summary>
/// Top-level automation editor view model. Owns the five-step wizard state, dirty tracking (via a
/// canonical-JSON fingerprint compared to the loaded baseline), optimistic-concurrency conflict
/// detection (AUT-008 via <c>ExpectedRowVersion</c>), space immutability (AUT-002: changing space after
/// load forces a save-as-copy), and the save/save-and-enable orchestration over
/// <see cref="IAutomationService"/>. Reuses the T05 validators and <see cref="ScheduleCalculator"/>
/// so there is exactly one algorithm per concern (AI dev rule §40). No WinUI, Repository, Connector,
/// Secret, or Native dependency (AI dev rule §3).
/// </summary>
public sealed class AutomationEditorSession : ObservableBase
{
    private readonly IAutomationService _service;
    private readonly IClock _clock;
    private readonly ITimeZoneResolver _tzResolver;

    private EditorMode _mode = EditorMode.New;
    private EditorStep _currentStep = EditorStep.BasicInfo;
    private SaveState _saveState = SaveState.Idle;
    private AppError? _lastError;
    private bool _hasConflict;
    private bool _warningsAcknowledged;
    private string? _baselineHash;

    private AutomationId? _id;
    private AutomationRevisionId? _currentRevisionId;
    private long _expectedRowVersion;
    private SpaceId? _loadedSpaceId;

    private string _name = string.Empty;
    private string _description = string.Empty;
    private SpaceId? _spaceId;
    private string? _projectId;
    private string? _expertId;
    private OverlapPolicy _overlapPolicy = OverlapPolicy.Skip;
    private MissedRunPolicy _missedRunPolicy = MissedRunPolicy.Skip;
    private RunBudget _budget = new(1, 4096, 300, 1, 4096);
    private PermissionRequest _permission = new(System.Array.Empty<string>(), "read-only");

    private TriggerEditorSession _triggerSession = null!;
    private WorkflowEditorSession _workflowSession = null!;

    private IReadOnlyList<PreflightCheck> _preflightChecks = System.Array.Empty<PreflightCheck>();
    private IReadOnlyList<TriggerPreviewItem> _triggerPreview = System.Array.Empty<TriggerPreviewItem>();

    public AutomationEditorSession(IAutomationService service, IClock clock, ITimeZoneResolver tzResolver)
    {
        _service = service ?? throw new System.ArgumentNullException(nameof(service));
        _clock = clock ?? throw new System.ArgumentNullException(nameof(clock));
        _tzResolver = tzResolver ?? throw new System.ArgumentNullException(nameof(tzResolver));

        SaveDraftCommand = new AsyncRelayCommand((_, ct) => SaveDraftAsync(ct));
        SaveAndEnableCommand = new AsyncRelayCommand((_, ct) => SaveAndEnableAsync(ct));
        RunPreflightCommand = new AsyncRelayCommand((_, _) => Task.Run(RunPreflight));
        PreviewTriggerCommand = new AsyncRelayCommand((_, _) => Task.Run(PreviewTrigger));
    }

    // ---- Identity / mode ----
    public EditorMode Mode => _mode;
    public AutomationId? Id => _id;
    public AutomationRevisionId? CurrentRevisionId => _currentRevisionId;
    public bool IsNew => _mode == EditorMode.New;

    // ---- Step navigation ----
    public EditorStep CurrentStep
    {
        get => _currentStep;
        set
        {
            if (Set(ref _currentStep, value))
                NotifyDerived();
        }
    }

    public bool CanMoveNext => _currentStep < EditorStep.TestEnable;
    public bool CanMoveBack => _currentStep > EditorStep.BasicInfo;
    public void GoNext() { if (CanMoveNext) CurrentStep = _currentStep + 1; }
    public void GoBack() { if (CanMoveBack) CurrentStep = _currentStep - 1; }

    // ---- Working fields ----
    public string Name { get => _name; set { if (Set(ref _name, value)) NotifyDerived(); } }
    public string Description { get => _description; set { if (Set(ref _description, value)) NotifyDerived(); } }
    public SpaceId? SpaceId { get => _spaceId; set { if (Set(ref _spaceId, value)) NotifyDerived(); } }
    public string? ProjectId { get => _projectId; set { if (Set(ref _projectId, value)) NotifyDerived(); } }
    public string? ExpertId { get => _expertId; set { if (Set(ref _expertId, value)) NotifyDerived(); } }
    public OverlapPolicy OverlapPolicy { get => _overlapPolicy; set { if (Set(ref _overlapPolicy, value)) NotifyDerived(); } }
    public MissedRunPolicy MissedRunPolicy { get => _missedRunPolicy; set { if (Set(ref _missedRunPolicy, value)) NotifyDerived(); } }
    public RunBudget Budget { get => _budget; set { if (Set(ref _budget, value)) NotifyDerived(); } }
    public PermissionRequest Permission { get => _permission; set { if (Set(ref _permission, value)) NotifyDerived(); } }

    public TriggerEditorSession TriggerSession
    {
        get => _triggerSession;
        private set { _triggerSession = value; Raise(nameof(TriggerSession)); Raise(nameof(IsDirty)); }
    }

    public WorkflowEditorSession WorkflowSession
    {
        get => _workflowSession;
        private set { _workflowSession = value; Raise(nameof(WorkflowSession)); Raise(nameof(IsDirty)); }
    }

    // ---- Save state ----
    public SaveState State { get => _saveState; private set { if (Set(ref _saveState, value)) NotifyDerived(); } }
    public AppError? LastError { get => _lastError; private set => Set(ref _lastError, value); }
    public bool HasConflict { get => _hasConflict; private set { if (Set(ref _hasConflict, value)) NotifyDerived(); } }
    public bool WarningsAcknowledged { get => _warningsAcknowledged; set { if (Set(ref _warningsAcknowledged, value)) NotifyDerived(); } }

    /// <summary>True when the working content differs from the loaded/saved baseline (canonical-JSON compare).</summary>
    public bool IsDirty => _baselineHash is null || !string.Equals(_baselineHash, ComputeWorkingHash(), System.StringComparison.Ordinal);

    /// <summary>Space changed after load => the automation must be saved as a NEW copy (AUT-002).</summary>
    public bool SpaceChangedAfterLoad => _mode == EditorMode.Edit && _loadedSpaceId is not null && !Equals(_spaceId, _loadedSpaceId);

    // ---- Computable validity (live, drives the enable button) ----
    public bool HasBlockingErrors
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_name) || _name.Trim().Length > Limits.V1_5.MaxAutomationNameLength) return true;
            if (_spaceId is null || _spaceId.Value == default) return true;
            if (string.IsNullOrWhiteSpace(_expertId)) return true;
            if (TriggerSession is null || TriggerSession.Validation.HasErrors) return true;
            if (WorkflowSession is null || WorkflowSession.Validation.HasErrors) return true;
            return false;
        }
    }

    public bool HasWarnings =>
        (TriggerSession?.Validation.HasWarnings ?? false) || (WorkflowSession?.Validation.HasWarnings ?? false);

    /// <summary>Enable is allowed only with zero errors and (if any warnings) explicit acknowledgement (doc 02 §4.5).</summary>
    public bool CanSaveAndEnable => !HasBlockingErrors && (!HasWarnings || _warningsAcknowledged) && State != SaveState.Saving && !HasConflict;

    // ---- Commands ----
    public AsyncRelayCommand SaveDraftCommand { get; }
    public AsyncRelayCommand SaveAndEnableCommand { get; }
    public AsyncRelayCommand RunPreflightCommand { get; }
    public AsyncRelayCommand PreviewTriggerCommand { get; }

    // ---- Preflight / preview ----
    public IReadOnlyList<PreflightCheck> PreflightChecks { get => _preflightChecks; private set => Set(ref _preflightChecks, value); }
    public IReadOnlyList<TriggerPreviewItem> TriggerPreview { get => _triggerPreview; private set => Set(ref _triggerPreview, value); }

    // ---- Loading ----
    /// <summary>Initializes a fresh automation in the given space with sane defaults.</summary>
    public void LoadNew(SpaceId space)
    {
        _mode = EditorMode.New;
        _id = null;
        _currentRevisionId = null;
        _expectedRowVersion = 0;
        _loadedSpaceId = space;
        _spaceId = space;
        _name = string.Empty;
        _description = string.Empty;
        _projectId = null;
        _expertId = null;
        _overlapPolicy = OverlapPolicy.Skip;
        _missedRunPolicy = MissedRunPolicy.Skip;
        _budget = new RunBudget(1, 4096, 300, 10, 32_000);
        _permission = new PermissionRequest(System.Array.Empty<string>(), "read-only");
        _triggerSession = new TriggerEditorSession(DefaultTrigger(_clock.UtcNow));
        _workflowSession = new WorkflowEditorSession(DefaultWorkflow());
        _hasConflict = false;
        _lastError = null;
        _baselineHash = ComputeWorkingHash();
        _currentStep = EditorStep.BasicInfo;
        State = SaveState.Idle;
        NotifyDerived();
    }

    /// <summary>Loads an existing automation (definition + current revision content) for editing.</summary>
    public async Task<Result> LoadExistingAsync(AutomationId id, CancellationToken ct = default)
    {
        var def = await _service.GetAsync(id, ct);
        if (!def.IsSuccess) { LastError = def.Error; State = SaveState.Error; return Result.Failure(def.Error!); }
        var rev = await _service.GetCurrentRevisionAsync(id, ct);
        if (!rev.IsSuccess) { LastError = rev.Error; State = SaveState.Error; return Result.Failure(rev.Error!); }

        _mode = EditorMode.Edit;
        _id = def.Value!.Id;
        _currentRevisionId = def.Value.CurrentRevisionId;
        _expectedRowVersion = def.Value.RowVersion;
        _loadedSpaceId = def.Value.SpaceId;
        _spaceId = def.Value.SpaceId;
        _name = def.Value.Name;
        _description = def.Value.Description;
        _projectId = rev.Value!.Binding.ProjectId;
        _expertId = rev.Value.Binding.ExpertId;
        _overlapPolicy = rev.Value.OverlapPolicy;
        _missedRunPolicy = rev.Value.MissedRunPolicy;
        _budget = rev.Value.Budget;
        _permission = rev.Value.PermissionRequest;
        _triggerSession = new TriggerEditorSession(rev.Value.Trigger);
        _workflowSession = new WorkflowEditorSession(rev.Value.Workflow);
        _hasConflict = false;
        _lastError = null;
        _baselineHash = ComputeWorkingHash();
        _currentStep = EditorStep.BasicInfo;
        State = SaveState.Idle;
        NotifyDerived();
        return Result.Success();
    }

    // ---- Save orchestration ----
    public async Task<EditorSaveResult> SaveDraftAsync(CancellationToken ct = default)
    {
        State = SaveState.Saving;
        var trigger = TriggerSession.Trigger;
        var workflow = WorkflowSession.Build();
        var binding = new AutomationBinding(_projectId, _expertId);

        Result<AutomationDefinition> result;
        if (_mode == EditorMode.New || SpaceChangedAfterLoad)
        {
            // New automation, OR space changed after load => save as a new copy (AUT-002).
            var req = new CreateAutomationRequest(_spaceId!.Value, _name, _description, trigger, workflow,
                binding, _budget, _overlapPolicy, _missedRunPolicy, _permission);
            result = await _service.CreateAsync(req, ct);
        }
        else
        {
            var req = new UpdateAutomationRequest(_id!.Value, _name, _description, _expectedRowVersion,
                trigger, workflow, binding, _budget, _overlapPolicy, _missedRunPolicy, _permission);
            result = await _service.UpdateDraftAsync(req, ct);
        }

        return FinalizeSave(result, ct);
    }

    public async Task<EditorSaveResult> SaveAndEnableAsync(CancellationToken ct = default)
    {
        if (HasBlockingErrors)
        {
            State = SaveState.Error;
            return new EditorSaveResult(false, false, LastError);
        }
        if (HasConflict)
        {
            State = SaveState.Conflict;
            return new EditorSaveResult(false, true, LastError);
        }

        var draft = await SaveDraftAsync(ct);
        if (!draft.Succeeded)
            return draft;

        // Publish the freshly saved (or created) current revision.
        var publish = await _service.PublishAsync(_id!.Value, _currentRevisionId!.Value, _expectedRowVersion, ct);
        if (!publish.IsSuccess)
        {
            if (IsConcurrencyConflict(publish.Error))
            {
                HasConflict = true;
                State = SaveState.Conflict;
                return new EditorSaveResult(false, true, publish.Error);
            }
            LastError = publish.Error;
            State = SaveState.Error;
            return new EditorSaveResult(false, false, publish.Error);
        }

        State = SaveState.Saved;
        LastError = null;
        return new EditorSaveResult(true, false, null);
    }

    /// <summary>Conflict resolution: discard local edits and rebase onto the server's current version.</summary>
    public async Task<Result> ReloadFromServerAsync(CancellationToken ct = default)
    {
        if (_mode != EditorMode.Edit || _id is null)
            return Result.Failure(AutomationErrors.NotFoundError());
        var r = await LoadExistingAsync(_id.Value, ct);
        HasConflict = false;
        return r;
    }

    public void RunPreflight()
    {
        if (TriggerSession is null || WorkflowSession is null)
            return;
        PreflightChecks = PreflightRunner.Run(new PreflightContext(
            _name, _spaceId, _expertId, TriggerSession.Trigger, WorkflowSession.Build()));
    }

    public void PreviewTrigger()
    {
        if (TriggerSession is null)
            return;
        TriggerPreview = TriggerPreviewProvider.ProjectNextOccurrences(TriggerSession.Trigger, _clock, _tzResolver, 10);
    }

    // ---- Internals ----
    private EditorSaveResult FinalizeSave(Result<AutomationDefinition> result, CancellationToken ct)
    {
        if (!result.IsSuccess)
        {
            if (IsConcurrencyConflict(result.Error))
            {
                HasConflict = true;
                State = SaveState.Conflict;
                return new EditorSaveResult(false, true, result.Error);
            }
            LastError = result.Error;
            State = SaveState.Error;
            return new EditorSaveResult(false, false, result.Error);
        }

        var def = result.Value!;
        _id = def.Id;
        _currentRevisionId = def.CurrentRevisionId;
        _expectedRowVersion = def.RowVersion;
        _loadedSpaceId = def.SpaceId;
        _spaceId = def.SpaceId;
        _baselineHash = ComputeWorkingHash(); // reset dirty baseline
        HasConflict = false;
        LastError = null;
        State = SaveState.Saved;
        _mode = EditorMode.Edit; // after any successful save the editor operates on a persisted automation
        NotifyDerived();
        return new EditorSaveResult(true, false, null);
    }

    private static bool IsConcurrencyConflict(AppError? error) =>
        error is not null && error.Code == "AUT_CONCURRENCY";

    private void NotifyDerived()
    {
        Raise(nameof(IsDirty));
        Raise(nameof(CanMoveNext));
        Raise(nameof(CanMoveBack));
        Raise(nameof(SpaceChangedAfterLoad));
        Raise(nameof(HasBlockingErrors));
        Raise(nameof(HasWarnings));
        Raise(nameof(CanSaveAndEnable));
        Raise(nameof(State));
    }

    private string ComputeWorkingHash()
    {
        if (TriggerSession is null || WorkflowSession is null)
            return string.Empty;
        var obj = new JsonObject
        {
            ["name"] = _name,
            ["description"] = _description,
            ["space_id"] = _spaceId?.Value,
            ["project_id"] = _projectId,
            ["expert_id"] = _expertId,
            ["overlap_policy"] = _overlapPolicy.ToStorage(),
            ["missed_run_policy"] = _missedRunPolicy.ToStorage(),
            ["budget"] = _budget.ToCanonicalJson(),
            ["permission_request"] = _permission.ToCanonicalJson(),
            ["trigger"] = TriggerSession.Trigger.ToCanonicalJson(),
            ["workflow"] = WorkflowSession.Build().ToCanonicalJson()
        };
        return JcsCanonicalizer.Canonicalize(obj);
    }

    private static TriggerDefinition DefaultTrigger(DateTimeOffset now) =>
        new("trigger", TriggerType.Interval, true, "UTC", null, null,
            Limits.V1_5.MinIntervalSeconds, now, null, null, null, null, null, null);

    private static WorkflowDefinition DefaultWorkflow()
    {
        var node = new WorkflowNode("n1", "Step 1", "agent_prompt", Limits.V1_5.MinWorkflowNodeTimeoutSeconds, false, null);
        return new WorkflowDefinition(1, "n1", new[] { node }, System.Array.Empty<WorkflowEdge>());
    }
}
