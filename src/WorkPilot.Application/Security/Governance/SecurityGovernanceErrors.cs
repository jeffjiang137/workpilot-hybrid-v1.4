using System.Collections.Generic;
using WorkPilot.Contracts.Primitives;

namespace WorkPilot.Application.Security.Governance;

/// <summary>
/// Error catalog for Security Center governance commands (SEC-101..107, PER-008). Registered
/// globally so codes stay unique across features (AI dev rule §13). Not-found uses
/// <see cref="ErrorCategory.Resource"/> (ErrorCategory has no NotFound member).
/// </summary>
public sealed class SecurityGovernanceErrors : FeatureErrorCatalog
{
    public override string Feature => "SecurityGovernance";

    public static readonly SecurityGovernanceErrors Instance = new();

    public static readonly ErrorDefinition IncidentNotFound = new("SEC_GOV_INCIDENT_NOT_FOUND", ErrorCategory.Resource, "SecurityGovernance.IncidentNotFound", false);
    public static readonly ErrorDefinition IncidentInvalidTransition = new("SEC_GOV_INCIDENT_INVALID_TRANSITION", ErrorCategory.Validation, "SecurityGovernance.IncidentInvalidTransition", false);
    public static readonly ErrorDefinition GrantNotFound = new("SEC_GOV_GRANT_NOT_FOUND", ErrorCategory.Resource, "SecurityGovernance.GrantNotFound", false);
    public static readonly ErrorDefinition GrantAlreadyRevoked = new("SEC_GOV_GRANT_ALREADY_REVOKED", ErrorCategory.Validation, "SecurityGovernance.GrantAlreadyRevoked", false);
    public static readonly ErrorDefinition ImpactChanged = new("SEC_GOV_IMPACT_CHANGED", ErrorCategory.Validation, "SecurityGovernance.ImpactChanged", false);
    public static readonly ErrorDefinition PartialFailure = new("SEC_GOV_PARTIAL_FAILURE", ErrorCategory.Internal, "SecurityGovernance.PartialFailure", false);
    public static readonly ErrorDefinition EmergencyStopActive = new("SEC_GOV_EMERGENCY_STOP_ACTIVE", ErrorCategory.Policy, "SecurityGovernance.EmergencyStopActive", false);
    public static readonly ErrorDefinition SourceNotFound = new("SEC_GOV_SOURCE_NOT_FOUND", ErrorCategory.Resource, "SecurityGovernance.SourceNotFound", false);

    static SecurityGovernanceErrors() => ErrorCatalog.Register(Instance);

    public override IReadOnlyList<ErrorDefinition> Definitions => new[]
    {
        IncidentNotFound, IncidentInvalidTransition, GrantNotFound, GrantAlreadyRevoked,
        ImpactChanged, PartialFailure, EmergencyStopActive, SourceNotFound
    };

    public static AppError IncidentNotFoundError(string id) =>
        Instance.Error(IncidentNotFound.Code, new Dictionary<string, string> { ["id"] = id });
    public static AppError IncidentInvalidTransitionError(string from, string to) =>
        Instance.Error(IncidentInvalidTransition.Code, new Dictionary<string, string> { ["from"] = @from, ["to"] = to });
    public static AppError GrantNotFoundError(string id) =>
        Instance.Error(GrantNotFound.Code, new Dictionary<string, string> { ["id"] = id });
    public static AppError GrantAlreadyRevokedError(string id) =>
        Instance.Error(GrantAlreadyRevoked.Code, new Dictionary<string, string> { ["id"] = id });
    public static AppError ImpactChangedError() =>
        Instance.Error(ImpactChanged.Code, new Dictionary<string, string> { ["detail"] = "影响自预览后已变化，请重新确认" });
    public static AppError PartialFailureError(string summary) =>
        Instance.Error(PartialFailure.Code, new Dictionary<string, string> { ["summary"] = summary });
    public static AppError EmergencyStopActiveError() =>
        Instance.Error(EmergencyStopActive.Code, new Dictionary<string, string> { ["detail"] = "紧急停止已处于激活状态" });
    public static AppError SourceNotFoundError(string kind, string id) =>
        Instance.Error(SourceNotFound.Code, new Dictionary<string, string> { ["kind"] = kind, ["id"] = id });
}
