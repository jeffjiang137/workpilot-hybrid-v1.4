namespace WorkPilot.Domain.PermissionGovernance.Evaluation;

/// <summary>
/// Stable, non-localized reason codes for policy decisions (doc 07 §16). These are part of the
/// decision contract: codes are never localized (UI uses MessageKey), and deleting/retconning a code
/// is a breaking change. The list is a minimum — positive-outcome codes (<see cref="AllowedByPolicy"/>,
/// <see cref="AskRequired"/>, <see cref="TimeWindowDeferred"/>) are added for completeness.
/// </summary>
public static class PolicyReasonCodes
{
    // Deny / defer reasons (doc 07 §16)
    public const string EmergencyStopActive = "EmergencyStopActive";
    public const string SourceDisabled = "SourceDisabled";
    public const string SourceQuarantined = "SourceQuarantined";
    public const string SpaceSourceNotEnabled = "SpaceSourceNotEnabled";
    public const string ExpertSourceNotGranted = "ExpertSourceNotGranted";
    public const string CapabilityNotAllowlisted = "CapabilityNotAllowlisted";
    public const string SchemaChanged = "SchemaChanged";
    public const string ArgumentsInvalid = "ArgumentsInvalid";
    public const string ResourceOutOfScope = "ResourceOutOfScope";
    public const string ExplicitDeny = "ExplicitDeny";
    public const string MissingAutomationGrant = "MissingAutomationGrant";
    public const string GrantExpired = "GrantExpired";
    public const string GrantRevoked = "GrantRevoked";
    public const string HighRequiresApproval = "HighRequiresApproval";
    public const string CriticalBlocked = "CriticalBlocked";
    public const string ApprovalExpired = "ApprovalExpired";
    public const string ReceiptConsumed = "ReceiptConsumed";
    public const string RevocationEpochChanged = "RevocationEpochChanged";
    public const string LeaseLost = "LeaseLost";
    public const string BudgetExceeded = "BudgetExceeded";
    public const string RateLimitedDeferred = "RateLimitedDeferred";
    public const string AuditUnavailable = "AuditUnavailable";

    // Positive-outcome / deferral codes (added; stable, not localized)
    public const string AllowedByPolicy = "AllowedByPolicy";
    public const string AskRequired = "AskRequired";
    public const string TimeWindowDeferred = "TimeWindowDeferred";
}
