using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.App.Core.Primitives;
using WorkPilot.Application.Security.Governance;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.PermissionGovernance;

namespace WorkPilot.App.Core.Security;

/// <summary>
/// Grants tab (doc 06 §6.3 / PER-008). Lists active grants and revokes them through the governance
/// command. Revoke is a two-step preview→apply flow: the preview returns an <see cref="GrantRevokePreview.ImpactToken"/>
/// that is recomputed at apply time. If the impact changed in between (grant state, epoch or active
/// grant count shifted) the apply is refused with <c>SEC_GOV_IMPACT_CHANGED</c> and the UI must
/// re-preview rather than act on stale analysis (doc 06 §10 Impact changed). No secret / connector /
/// native dependency.
/// </summary>
public sealed class GrantListViewModel : ObservableBase
{
    private readonly ISecurityCenterDataProvider _provider;

    private bool _isLoading;
    private AppError? _error;
    private PolicyGrant? _selected;
    private bool _impactChanged;
    private string? _pendingImpactSummary;
    private readonly Dictionary<PolicyGrantId, string> _pendingTokens = new();

    private readonly ObservableCollection<PolicyGrant> _grants = new();

    public GrantListViewModel(ISecurityCenterDataProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        RefreshCommand = new AsyncRelayCommand((_, ct) => LoadAsync(ct));
        PreviewCommand = new AsyncRelayCommand((p, ct) => PreviewRevokeAsync((PolicyGrantId)p!, ct));
        RevokeCommand = new AsyncRelayCommand((p, ct) => RevokeAsync((PolicyGrantId)p!, ct));
    }

    public ObservableCollection<PolicyGrant> Grants => _grants;

    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }
    public AppError? Error { get => _error; private set { if (Set(ref _error, value)) Raise(nameof(HasError)); } }
    public bool HasError => _error is not null;
    public bool IsEmpty => !_isLoading && _error is null && _grants.Count == 0;

    public PolicyGrant? SelectedGrant { get => _selected; private set => Set(ref _selected, value); }
    /// <summary>True when an apply was refused because the impact changed since preview (doc 06 §10).</summary>
    public bool ImpactChanged { get => _impactChanged; private set => Set(ref _impactChanged, value); }
    public string? PendingImpactSummary { get => _pendingImpactSummary; private set => Set(ref _pendingImpactSummary, value); }

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand PreviewCommand { get; }
    public AsyncRelayCommand RevokeCommand { get; }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        IsLoading = true; Error = null; ImpactChanged = false;
        try
        {
            var res = await _provider.ListActiveGrantsAsync(DateTimeOffset.UtcNow, ct);
            if (!res.IsSuccess) { Error = res.Error; return; }
            _grants.Clear();
            foreach (var g in res.Value!) _grants.Add(g);
        }
        finally { IsLoading = false; Raise(nameof(IsEmpty)); }
    }

    /// <summary>Step 1: preview the revoke and capture the impact token (PER-008 second confirmation).</summary>
    public async Task<bool> PreviewRevokeAsync(PolicyGrantId grantId, CancellationToken ct = default)
    {
        ImpactChanged = false; PendingImpactSummary = null;
        var res = await _provider.PreviewRevokeAsync(grantId, ct);
        if (!res.IsSuccess) { Error = res.Error; return false; }
        _pendingTokens[grantId] = res.Value!.ImpactToken;
        PendingImpactSummary = $"capability={res.Value.CapabilityStableId}; automation={res.Value.AutomationId}";
        return true;
    }

    /// <summary>Step 2: apply the revoke using the previously captured token. Refuses on impact change.</summary>
    public async Task<bool> RevokeAsync(PolicyGrantId grantId, CancellationToken ct = default)
    {
        if (!_pendingTokens.TryGetValue(grantId, out var token))
        {
            Error = new AppError("SEC_GOV_NO_PREVIEW", ErrorCategory.Validation,
                "SecurityGovernance.NoPreview", false);
            return false;
        }
        var res = await _provider.RevokeGrantAsync(grantId, token, ct);
        if (!res.IsSuccess)
        {
            Error = res.Error;
            if (res.Error?.Code == SecurityGovernanceErrors.ImpactChanged.Code)
            {
                // Stale impact analysis — force re-preview (doc 06 §10). Do NOT clear the error silently.
                ImpactChanged = true;
                _pendingTokens.Remove(grantId);
                PendingImpactSummary = null;
            }
            return false;
        }
        _pendingTokens.Remove(grantId);
        PendingImpactSummary = null;
        ImpactChanged = false;
        await LoadAsync(ct);
        return true;
    }
}
