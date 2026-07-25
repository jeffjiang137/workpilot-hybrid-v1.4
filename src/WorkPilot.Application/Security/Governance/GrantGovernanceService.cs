using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Application.Permission.Policy;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.PermissionGovernance;
using WorkPilot.Domain.Security.Audit;

namespace WorkPilot.Application.Security.Governance;

/// <summary>
/// Revoke-grant governance command (PER-008). Implements the doc 06 §10 "Impact changed" rule:
/// a preview returns an <see cref="GrantRevokePreview.ImpactToken"/>; revoke only applies if the
/// token still matches — if the grant state, epoch or active-grant count shifted meanwhile, the
/// command is refused so the operator re-confirms instead of acting on stale impact analysis.
/// Revoking bumps the revocation epoch so any in-flight permit/receipt/grant fails its
/// Current-State Check (doc 07 §11/§17).
/// </summary>
public sealed class GrantGovernanceService
{
    private readonly IGrantStore _grants;
    private readonly IRevocationEpoch _epoch;
    private readonly IClock _clock;
    private readonly AuditLogWriter _audit;

    public GrantGovernanceService(IGrantStore grants, IRevocationEpoch epoch, IClock clock, AuditLogWriter audit)
    {
        _grants = grants;
        _epoch = epoch;
        _clock = clock;
        _audit = audit;
    }

    private async Task<string> ImpactTokenAsync(PolicyGrantId id, CancellationToken ct)
    {
        var get = await _grants.GetAsync(id, ct);
        var grant = get.IsSuccess ? get.Value : null;
        var active = await _grants.ListActiveGrantsAsync(
            grant?.CapabilityStableId ?? string.Empty,
            grant?.SourceKind ?? string.Empty,
            grant?.SourceId ?? string.Empty,
            grant?.SchemaSha256 ?? string.Empty,
            _clock.UtcNow, ct);
        var activeCount = active.IsSuccess ? (active.Value?.Count ?? 0) : 0;
        var snapshot = $"{id.Value}|{grant?.RevokedAtUtc}|{_epoch.Current}|{activeCount}";
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(snapshot))).ToLowerInvariant();
    }

    public async Task<Result<GrantRevokePreview>> PreviewRevokeAsync(PolicyGrantId id, CancellationToken ct)
    {
        var get = await _grants.GetAsync(id, ct);
        if (!get.IsSuccess) return Result<GrantRevokePreview>.Fail(get.Error!);
        var grant = get.Value!;
        if (grant.RevokedAtUtc is not null) return Result<GrantRevokePreview>.Fail(SecurityGovernanceErrors.GrantAlreadyRevokedError(id.Value));
        var token = await ImpactTokenAsync(id, ct);
        return Result<GrantRevokePreview>.Ok(new GrantRevokePreview(token, grant.CapabilityStableId, grant.AutomationId));
    }

    public async Task<Result> RevokeAsync(PolicyGrantId id, string previewToken, CancellationToken ct)
    {
        var get = await _grants.GetAsync(id, ct);
        if (!get.IsSuccess) return Result.Failure(get.Error!);
        var grant = get.Value!;
        if (grant.RevokedAtUtc is not null) return Result.Failure(SecurityGovernanceErrors.GrantAlreadyRevokedError(id.Value));

        var current = await ImpactTokenAsync(id, ct);
        if (!string.Equals(current, previewToken, StringComparison.Ordinal))
            return Result.Failure(SecurityGovernanceErrors.ImpactChangedError());

        var revoked = await _grants.RevokeAsync(id, _clock, ct);
        if (!revoked.IsSuccess) return Result.Failure(revoked.Error!);

        _epoch.Bump();
        await _audit.AppendAsync(
            AuditCategory.Governance, "grant.revoked", "security_center",
            $"{{\"grant_id\":\"{id.Value}\"}}", "{\"decision\":\"grant revoked; revocation epoch bumped\"}",
            "{\"detail\":\"operator-initiated grant revocation\"}", ct);
        return Result.Success();
    }
}
