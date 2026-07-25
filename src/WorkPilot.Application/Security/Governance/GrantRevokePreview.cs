namespace WorkPilot.Application.Security.Governance;

/// <summary>
/// Snapshot returned by <see cref="GrantGovernanceService.PreviewRevokeAsync"/>. The
/// <see cref="ImpactToken"/> is recomputed at apply-time; if it differs the revoke is refused
/// (doc 06 §10: "Impact changed" must be re-confirmed, never silently applied).
/// </summary>
public sealed record GrantRevokePreview(
    string ImpactToken,
    string CapabilityStableId,
    string AutomationId);
