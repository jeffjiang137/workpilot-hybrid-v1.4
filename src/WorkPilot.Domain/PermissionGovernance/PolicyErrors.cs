using System.Collections.Generic;
using System.Collections.Immutable;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;

namespace WorkPilot.Domain.PermissionGovernance;

/// <summary>
/// Versioned error catalog for the Permission Governance feature (PER-* codes). Registered globally
/// so the catalog enforces cross-feature code uniqueness (AI dev rule §13: codes are immutable once
/// published). Security-relevant failures use <see cref="ErrorCategory.Policy"/>; not-found uses
/// <see cref="ErrorCategory.Resource"/> (ErrorCategory has no NotFound member).
/// </summary>
public sealed class PolicyErrors : FeatureErrorCatalog
{
    public override string Feature => "Policy";

    public static readonly ErrorDefinition NotFound = new("POLICY_NOT_FOUND", ErrorCategory.Resource, "Policy.NotFound", false);
    public static readonly ErrorDefinition VersionNotFound = new("POLICY_VERSION_NOT_FOUND", ErrorCategory.Resource, "Policy.VersionNotFound", false);
    public static readonly ErrorDefinition DocumentExists = new("POLICY_DOCUMENT_EXISTS", ErrorCategory.Conflict, "Policy.DocumentExists", false);
    public static readonly ErrorDefinition StatementInvalid = new("POLICY_STATEMENT_INVALID", ErrorCategory.Validation, "Policy.StatementInvalid", false);
    public static readonly ErrorDefinition ConditionInvalid = new("POLICY_CONDITION_INVALID", ErrorCategory.Validation, "Policy.ConditionInvalid", false);
    public static readonly ErrorDefinition ConditionCount = new("POLICY_CONDITION_COUNT", ErrorCategory.Validation, "Policy.ConditionCount", false);
    public static readonly ErrorDefinition RiskRange = new("POLICY_RISK_RANGE", ErrorCategory.Validation, "Policy.RiskRange", false);
    public static readonly ErrorDefinition WildcardAllow = new("POLICY_WILDCARD_ALLOW", ErrorCategory.Policy, "Policy.WildcardAllow", false);
    public static readonly ErrorDefinition ReceiptSuperseded = new("POLICY_RECEIPT_SUPERSEDED", ErrorCategory.Policy, "Policy.ReceiptSuperseded", false);
    public static readonly ErrorDefinition RecoveryFailed = new("POLICY_RECOVERY_FAILED", ErrorCategory.Internal, "Policy.RecoveryFailed", false);
    public static readonly ErrorDefinition AuditIntegrity = new("POLICY_AUDIT_INTEGRITY", ErrorCategory.Policy, "Policy.AuditIntegrity", false);

    // AutomationGrant (PER-004)
    public static readonly ErrorDefinition GrantNotFound = new("POLICY_GRANT_NOT_FOUND", ErrorCategory.Resource, "Policy.GrantNotFound", false);
    public static readonly ErrorDefinition GrantRiskCeiling = new("POLICY_GRANT_RISK_CEILING", ErrorCategory.Validation, "Policy.GrantRiskCeiling", false);
    public static readonly ErrorDefinition GrantDuration = new("POLICY_GRANT_DURATION", ErrorCategory.Validation, "Policy.GrantDuration", false);
    public static readonly ErrorDefinition GrantTimeRange = new("POLICY_GRANT_TIME_RANGE", ErrorCategory.Validation, "Policy.GrantTimeRange", false);
    public static readonly ErrorDefinition GrantRevokedBeforeCreated = new("POLICY_GRANT_REVOKED_BEFORE_CREATED", ErrorCategory.Validation, "Policy.GrantRevokedBeforeCreated", false);
    public static readonly ErrorDefinition GrantAlreadyRevoked = new("POLICY_GRANT_ALREADY_REVOKED", ErrorCategory.Conflict, "Policy.GrantAlreadyRevoked", false);
    public static readonly ErrorDefinition GrantExpired = new("POLICY_GRANT_EXPIRED", ErrorCategory.Policy, "Policy.GrantExpired", false);
    public static readonly ErrorDefinition GrantScopeExceedsManifest = new("POLICY_GRANT_SCOPE_EXCEEDS_MANIFEST", ErrorCategory.Policy, "Policy.GrantScopeExceedsManifest", false);
    public static readonly ErrorDefinition GrantSchemaMismatch = new("POLICY_GRANT_SCHEMA_MISMATCH", ErrorCategory.Policy, "Policy.GrantSchemaMismatch", false);
    public static readonly ErrorDefinition ImpactIncomplete = new("POLICY_IMPACT_INCOMPLETE", ErrorCategory.Validation, "Policy.ImpactIncomplete", false);
    public static readonly ErrorDefinition ExpansionRequiresConfirmation = new("POLICY_EXPANSION_REQUIRES_CONFIRMATION", ErrorCategory.Policy, "Policy.ExpansionRequiresConfirmation", false);

    public override IReadOnlyList<ErrorDefinition> Definitions => new[]
    {
        NotFound, VersionNotFound, DocumentExists, StatementInvalid, ConditionInvalid,
        ConditionCount, RiskRange, WildcardAllow, ReceiptSuperseded, RecoveryFailed, AuditIntegrity,
        GrantNotFound, GrantRiskCeiling, GrantDuration, GrantTimeRange, GrantRevokedBeforeCreated,
        GrantAlreadyRevoked, GrantExpired, GrantScopeExceedsManifest, GrantSchemaMismatch, ImpactIncomplete,
        ExpansionRequiresConfirmation
    };

    public static readonly PolicyErrors Instance = new();

    static PolicyErrors() => ErrorCatalog.Register(Instance);

    public static AppError NotFoundError() => Instance.Error("POLICY_NOT_FOUND");
    public static AppError VersionNotFoundError(PolicyVersionId id)
        => Instance.Error("POLICY_VERSION_NOT_FOUND", new Dictionary<string, string> { ["version_id"] = id.Value });
    public static AppError DocumentExistsError(string layer, string? scopeId)
        => Instance.Error("POLICY_DOCUMENT_EXISTS", new Dictionary<string, string> { ["layer"] = layer, ["scope_id"] = scopeId ?? "" });
    public static AppError StatementInvalidError()
        => Instance.Error("POLICY_STATEMENT_INVALID");
    public static AppError ConditionInvalidError()
        => Instance.Error("POLICY_CONDITION_INVALID");
    public static AppError ConditionCountError(int count)
        => Instance.Error("POLICY_CONDITION_COUNT", new Dictionary<string, string> { ["count"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture) });
    public static AppError RiskRangeError()
        => Instance.Error("POLICY_RISK_RANGE");
    public static AppError WildcardAllowError()
        => Instance.Error("POLICY_WILDCARD_ALLOW");
    public static AppError ReceiptSupersededError(string policyHash)
        => Instance.Error("POLICY_RECEIPT_SUPERSEDED", new Dictionary<string, string> { ["policy_hash"] = policyHash });
    public static AppError RecoveryFailedError(string detail)
        => Instance.Error("POLICY_RECOVERY_FAILED", new Dictionary<string, string> { ["detail"] = detail });
    public static AppError AuditIntegrityError(string detail)
        => Instance.Error("POLICY_AUDIT_INTEGRITY", new Dictionary<string, string> { ["detail"] = detail });

    // Grant helpers (PER-004)
    public static AppError GrantNotFoundError(string grantId)
        => Instance.Error("POLICY_GRANT_NOT_FOUND", new Dictionary<string, string> { ["grant_id"] = grantId });
    public static AppError GrantRiskCeilingError()
        => Instance.Error("POLICY_GRANT_RISK_CEILING");
    public static AppError GrantDurationError()
        => Instance.Error("POLICY_GRANT_DURATION");
    public static AppError GrantTimeRangeError()
        => Instance.Error("POLICY_GRANT_TIME_RANGE");
    public static AppError GrantRevokedBeforeCreatedError()
        => Instance.Error("POLICY_GRANT_REVOKED_BEFORE_CREATED");
    public static AppError GrantAlreadyRevokedError(string grantId)
        => Instance.Error("POLICY_GRANT_ALREADY_REVOKED", new Dictionary<string, string> { ["grant_id"] = grantId });
    public static AppError GrantExpiredError(string grantId)
        => Instance.Error("POLICY_GRANT_EXPIRED", new Dictionary<string, string> { ["grant_id"] = grantId });
    public static AppError GrantScopeExceedsManifestError(string capabilityStableId)
        => Instance.Error("POLICY_GRANT_SCOPE_EXCEEDS_MANIFEST", new Dictionary<string, string> { ["capability"] = capabilityStableId });
    public static AppError GrantSchemaMismatchError(string grantSchema, string capabilitySchema)
        => Instance.Error("POLICY_GRANT_SCHEMA_MISMATCH", new Dictionary<string, string> { ["grant_schema"] = grantSchema, ["capability_schema"] = capabilitySchema });
    public static AppError ImpactIncompleteError(int targetCount)
        => Instance.Error("POLICY_IMPACT_INCOMPLETE", new Dictionary<string, string> { ["targets"] = targetCount.ToString(System.Globalization.CultureInfo.InvariantCulture) });
    public static AppError ExpansionRequiresConfirmationError(int affectedGrants)
        => Instance.Error("POLICY_EXPANSION_REQUIRES_CONFIRMATION", new Dictionary<string, string> { ["affected_grants"] = affectedGrants.ToString(System.Globalization.CultureInfo.InvariantCulture) });
}
