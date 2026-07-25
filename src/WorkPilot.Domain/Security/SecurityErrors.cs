using System.Collections.Generic;
using System.Collections.Immutable;
using WorkPilot.Contracts.Primitives;

namespace WorkPilot.Domain.Security;

/// <summary>
/// Versioned error catalog for the Security Center feature (SEC-* codes). Registered globally so the
/// catalog enforces cross-feature code uniqueness (AI dev rule §13). Security-relevant failures use
/// <see cref="ErrorCategory.Policy"/> / <see cref="ErrorCategory.Internal"/>; not-found uses
/// <see cref="ErrorCategory.Resource"/> (ErrorCategory has no NotFound member).
/// </summary>
public sealed class SecurityErrors : FeatureErrorCatalog
{
    public override string Feature => "Security";

    public static readonly ErrorDefinition EventInvalid = new("SEC_EVENT_INVALID", ErrorCategory.Validation, "Security.EventInvalid", false);
    public static readonly ErrorDefinition IncidentNotFound = new("SEC_INCIDENT_NOT_FOUND", ErrorCategory.Resource, "Security.IncidentNotFound", false);
    public static readonly ErrorDefinition IncidentResolveRequiresCode = new("SEC_INCIDENT_RESOLVE_REQUIRES_CODE", ErrorCategory.Validation, "Security.IncidentResolveRequiresCode", false);
    public static readonly ErrorDefinition IncidentNoteTooLong = new("SEC_INCIDENT_NOTE_TOO_LONG", ErrorCategory.Validation, "Security.IncidentNoteTooLong", false);
    public static readonly ErrorDefinition AuditWriteFailed = new("SEC_AUDIT_WRITE_FAILED", ErrorCategory.Database, "Security.AuditWriteFailed", false);
    public static readonly ErrorDefinition AuditIntegrityFailed = new("SEC_AUDIT_INTEGRITY_FAILED", ErrorCategory.Policy, "Security.AuditIntegrityFailed", false);
    public static readonly ErrorDefinition DetectorActionFailed = new("SEC_DETECTOR_ACTION_FAILED", ErrorCategory.Internal, "Security.DetectorActionFailed", false);
    public static readonly ErrorDefinition EventSinkUnavailable = new("SEC_EVENT_SINK_UNAVAILABLE", ErrorCategory.Internal, "Security.EventSinkUnavailable", false);

    public override IReadOnlyList<ErrorDefinition> Definitions => new[]
    {
        EventInvalid, IncidentNotFound, IncidentResolveRequiresCode, IncidentNoteTooLong,
        AuditWriteFailed, AuditIntegrityFailed, DetectorActionFailed, EventSinkUnavailable
    };

    public static readonly SecurityErrors Instance = new();

    static SecurityErrors() => ErrorCatalog.Register(Instance);

    public static AppError EventInvalidError(string detail)
        => Instance.Error("SEC_EVENT_INVALID", new Dictionary<string, string> { ["detail"] = detail });
    public static AppError IncidentNotFoundError(string id)
        => Instance.Error("SEC_INCIDENT_NOT_FOUND", new Dictionary<string, string> { ["incident_id"] = id });
    public static AppError IncidentResolveRequiresCodeError()
        => Instance.Error("SEC_INCIDENT_RESOLVE_REQUIRES_CODE");
    public static AppError IncidentNoteTooLongError(int length)
        => Instance.Error("SEC_INCIDENT_NOTE_TOO_LONG", new Dictionary<string, string> { ["length"] = length.ToString(System.Globalization.CultureInfo.InvariantCulture) });
    public static AppError AuditWriteFailedError(string detail)
        => Instance.Error("SEC_AUDIT_WRITE_FAILED", new Dictionary<string, string> { ["detail"] = detail });
    public static AppError AuditIntegrityFailedError(string detail)
        => Instance.Error("SEC_AUDIT_INTEGRITY_FAILED", new Dictionary<string, string> { ["detail"] = detail });
    public static AppError DetectorActionFailedError(string detail)
        => Instance.Error("SEC_DETECTOR_ACTION_FAILED", new Dictionary<string, string> { ["detail"] = detail });
    public static AppError EventSinkUnavailableError()
        => Instance.Error("SEC_EVENT_SINK_UNAVAILABLE");
}
