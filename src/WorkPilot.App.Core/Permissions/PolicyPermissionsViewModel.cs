using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Application.Permission.Policy;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.PermissionGovernance;
using WorkPilot.Domain.PermissionGovernance.Evaluation;
using WorkPilot.App.Core.Primitives;

namespace WorkPilot.App.Core.Permissions;

/// <summary>
/// BCL view-model for the permission page (PER-003/004/008–010, T18). It orchestrates the
/// effective-permission projection, impact analysis, second-confirmation save, and grant listing/
/// revocation over the Application policy services — keeping all governance logic off the WinUI thread
/// and free of XAML / secret / native dependencies (AI dev rule §3). The WinUI <c>PermissionsPage</c>
/// binds to this type; only this BCL assembly is compiled/tested in CI, the WinUI project is gated to a
/// real Windows build (doc 10 §16).
/// </summary>
public sealed class PolicyPermissionsViewModel : ObservableBase
{
    private readonly IPolicyStore _store;
    private readonly PolicyProjectionService _projection;
    private readonly PolicyAdminService _admin;
    private readonly IGrantStore _grants;
    private readonly IClock _clock;

    private ObservableCollection<EffectiveCapabilityView> _effectivePermissions = new();
    private ObservableCollection<PolicyGrant> _activeGrants = new();
    private PolicyImpactReport? _pendingImpact;
    private bool _requiresConfirmation;
    private string? _lastError;
    private bool _isBusy;

    // Save context captured at preview time and consumed on confirmation.
    private PolicyLayer _editedLayer = PolicyLayer.BuiltInSafety;
    private string? _scopeId;
    private IReadOnlyList<PolicyStatement>? _newStatements;
    private IReadOnlyList<ImpactTarget>? _targets;
    private string _actor = "user";

    // Projection context cached from the page before the user triggers ProjectCommand.
    private EvaluationContext? _projectionContext;
    private IReadOnlyList<CapabilityQuery>? _projectionQueries;

    /// <summary>Captures the subject/scopes to project so the bound command can run without args.</summary>
    public void SetProjectionContext(EvaluationContext context, IReadOnlyList<CapabilityQuery> queries)
    {
        _projectionContext = context;
        _projectionQueries = queries;
    }

    /// <summary>Captures the proposed edit so the bound command can run without args (PER-008/015).</summary>
    public void SetPendingEdit(
        PolicyLayer editedLayer, string? scopeId,
        IReadOnlyList<PolicyStatement> newStatements, IReadOnlyList<ImpactTarget> targets, string actor)
    {
        _editedLayer = editedLayer;
        _scopeId = scopeId;
        _newStatements = newStatements;
        _targets = targets;
        _actor = actor;
    }

    public PolicyPermissionsViewModel(
        IPolicyStore store,
        PolicyProjectionService projection,
        PolicyAdminService admin,
        IGrantStore grants,
        IClock clock)
    {
        _store = store;
        _projection = projection;
        _admin = admin;
        _grants = grants;
        _clock = clock;
    }

    public ObservableCollection<EffectiveCapabilityView> EffectivePermissions
    { get => _effectivePermissions; set => Set(ref _effectivePermissions, value); }
    public ObservableCollection<PolicyGrant> ActiveGrants
    { get => _activeGrants; set => Set(ref _activeGrants, value); }
    public PolicyImpactReport? PendingImpact
    {
        get => _pendingImpact;
        set
        {
            if (!Set(ref _pendingImpact, value))
                return;
            Raise(nameof(HasPendingImpact));
            Raise(nameof(ImpactSummary));
        }
    }
    /// <summary>True when a save has been previewed and is awaiting confirmation (PER-008 second-confirm gate).</summary>
    public bool HasPendingImpact => _pendingImpact is not null;
    /// <summary>Human-readable summary of the last previewed impact (bound to the page).</summary>
    public string ImpactSummary
    {
        get
        {
            if (_pendingImpact is null)
                return "尚未预览策略变更。";
            var r = _pendingImpact;
            return $"扩大权限: {r.HasPrivilegeExpansion}；需 epoch bump: {r.RequiresEpochBump}；" +
                   $"受影响授权: {r.AffectedGrantCount}；队列运行: {r.QueuedRunCount}；待审批: {r.PendingApprovalCount}";
        }
    }
    public bool RequiresConfirmation
    { get => _requiresConfirmation; set => Set(ref _requiresConfirmation, value); }
    public string? LastError
    { get => _lastError; set => Set(ref _lastError, value); }
    public bool IsBusy
    { get => _isBusy; set => Set(ref _isBusy, value); }

    public AsyncRelayCommand LoadGrantsCommand => new(async (p, ct) => await LoadGrantsAsync(ct));
    public AsyncRelayCommand ProjectCommand => new(async (p, ct) =>
    {
        if (_projectionContext is not null && _projectionQueries is not null)
            await ProjectAsync(_projectionContext, _projectionQueries, ct);
    });
    public AsyncRelayCommand PrepareSaveCommand => new(async (p, ct) =>
    {
        if (_newStatements is not null && _targets is not null)
            await PrepareSaveAsync(_editedLayer, _scopeId, _newStatements, _targets, _actor, ct);
    });
    public AsyncRelayCommand ConfirmSaveCommand => new(async (p, ct) => await ConfirmAndSaveAsync(ct));
    public AsyncRelayCommand RevokeGrantCommand => new(async (p, ct) => await RevokeGrantAsync((PolicyGrantId)p!, ct));

    /// <summary>Loads active grants for the current capability/source filter (grant到期/撤销 view, PER-004).</summary>
    public async Task LoadGrantsAsync(CancellationToken ct = default)
    {
        IsBusy = true; LastError = null;
        try
        {
            var res = await _grants.ListActiveGrantsAsync(
                _capabilityFilter ?? string.Empty, _sourceKindFilter ?? string.Empty,
                _sourceIdFilter ?? string.Empty, _schemaFilter ?? string.Empty, _clock.UtcNow, ct);
            if (res.IsSuccess)
                ActiveGrants = new ObservableCollection<PolicyGrant>(res.Value!);
            else
                LastError = res.Error?.MessageKey;
        }
        catch (Exception ex) { LastError = ex.Message; }
        finally { IsBusy = false; }
    }

    private string? _capabilityFilter, _sourceKindFilter, _sourceIdFilter, _schemaFilter;
    public void SetGrantFilter(string? capabilityStableId, string? sourceKind, string? sourceId, string? schemaSha256)
    {
        _capabilityFilter = capabilityStableId;
        _sourceKindFilter = sourceKind;
        _sourceIdFilter = sourceId;
        _schemaFilter = schemaSha256;
    }

    /// <summary>Runs the effective-permission projection for the selected subject/capabilities (PER-003).</summary>
    public async Task ProjectAsync(EvaluationContext context, IReadOnlyList<CapabilityQuery> queries, CancellationToken ct = default)
    {
        IsBusy = true; LastError = null;
        try
        {
            var views = await _projection.ProjectAsync(context, queries, ct);
            EffectivePermissions = new ObservableCollection<EffectiveCapabilityView>(views);
        }
        catch (Exception ex) { LastError = ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Previews the impact of a proposed policy edit (PER-008/015). Captures the save context, runs the
    /// analyzer, and flags <see cref="RequiresConfirmation"/> when the change would widen access so the
    /// UI can prompt for a second confirmation before <see cref="ConfirmAndSaveAsync"/>.
    /// </summary>
    public async Task<PolicyImpactReport?> PrepareSaveAsync(
        PolicyLayer editedLayer, string? scopeId,
        IReadOnlyList<PolicyStatement> newStatements, IReadOnlyList<ImpactTarget> targets,
        string actor, CancellationToken ct = default)
    {
        IsBusy = true; LastError = null;
        try
        {
            _editedLayer = editedLayer;
            _scopeId = scopeId;
            _newStatements = newStatements;
            _targets = targets;
            _actor = actor;

            var res = await _admin.PreviewImpactAsync(editedLayer, scopeId, newStatements, targets, _clock, ct);
            if (!res.IsSuccess)
            {
                LastError = res.Error?.MessageKey;
                PendingImpact = null;
                RequiresConfirmation = false;
                return null;
            }
            var report = res.Value!;
            PendingImpact = report;
            RequiresConfirmation = report.HasPrivilegeExpansion;
            return report;
        }
        catch (Exception ex) { LastError = ex.Message; return null; }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Commits the previously-previewed edit. Always passes <c>confirmedExpansion: true</c> — the
    /// second confirmation is the user acknowledging <see cref="RequiresConfirmation"/> in the UI. The
    /// admin service re-runs the gate and (on widening) bumps the revocation epoch (doc 07 §15/§17).
    /// </summary>
    public async Task<bool> ConfirmAndSaveAsync(CancellationToken ct = default)
    {
        if (_newStatements is null || _targets is null)
        {
            LastError = "No pending save to confirm.";
            return false;
        }
        IsBusy = true; LastError = null;
        try
        {
            var res = await _admin.SavePolicyAsync(
                _editedLayer, _scopeId, _newStatements, _targets, _actor,
                confirmedExpansion: true, _clock, ct: ct);
            if (!res.IsSuccess)
            {
                LastError = res.Error?.MessageKey;
                return false;
            }
            PendingImpact = null;
            RequiresConfirmation = false;
            _newStatements = null;
            _targets = null;
            return true;
        }
        catch (Exception ex) { LastError = ex.Message; return false; }
        finally { IsBusy = false; }
    }

    /// <summary>Revokes an active automation grant (PER-004). The grant row is never deleted (audit).</summary>
    public async Task<bool> RevokeGrantAsync(PolicyGrantId grantId, CancellationToken ct = default)
    {
        IsBusy = true; LastError = null;
        try
        {
            var res = await _grants.RevokeAsync(grantId, _clock, ct);
            if (!res.IsSuccess)
            {
                LastError = res.Error?.MessageKey;
                return false;
            }
            await LoadGrantsAsync(ct);
            return true;
        }
        catch (Exception ex) { LastError = ex.Message; return false; }
        finally { IsBusy = false; }
    }
}
