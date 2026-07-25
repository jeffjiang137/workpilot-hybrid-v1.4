namespace WorkPilot.Domain.Security;

/// <summary>
/// Severity of a security event / incident. Mirrors the fixed scale in doc 06 §5
/// (Info=0 … Critical=4). Values are strictly ordered so aggregation may only raise severity.
/// </summary>
public enum SecuritySeverity : int
{
    Info = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Critical = 4
}

/// <summary>Fixed detector rule identifiers (doc 06 §4, DET-001 … DET-016).</summary>
public static class DetectorId
{
    public const string AuthFailureContinuous = "DET-001";
    public const string McpSchemaChanged = "DET-002";
    public const string McpProtocolExceeded = "DET-003";
    public const string ExecutableHashChanged = "DET-004";
    public const string OAuthMismatch = "DET-005";
    public const string DpapiFailure = "DET-006";
    public const string RedactionCanaryHit = "DET-007";
    public const string AuditIntegrityFailure = "DET-008";
    public const string PolicyDenialBurst = "DET-009";
    public const string WorkerCrashRecoveryBurst = "DET-010";
    public const string QueueBackpressure = "DET-011";
    public const string DiskSpaceLow = "DET-012";
    public const string CapabilityNoPermit = "DET-013";
    public const string LeaseLostSendAttempt = "DET-014";
    public const string OutcomeUnknownWrite = "DET-015";
    public const string ApprovalRejectionBurst = "DET-016";
}

/// <summary>
/// The 16 fixed event types emitted by detectors (doc 06 §4). Each maps 1:1 to a
/// <see cref="DetectorId"/> rule. The enum is the stable, display-name-free identifier stored in
/// the fingerprint and audit trail.
/// </summary>
public enum SecurityEventType : int
{
    AuthFailureContinuous = 0,
    McpSchemaChanged = 1,
    McpProtocolExceeded = 2,
    ExecutableHashChanged = 3,
    OAuthMismatch = 4,
    DpapiFailure = 5,
    RedactionCanaryHit = 6,
    AuditIntegrityFailure = 7,
    PolicyDenialBurst = 8,
    WorkerCrashRecoveryBurst = 9,
    QueueBackpressure = 10,
    DiskSpaceLow = 11,
    CapabilityNoPermit = 12,
    LeaseLostSendAttempt = 13,
    OutcomeUnknownWrite = 14,
    ApprovalRejectionBurst = 15
}

/// <summary>Lifecycle of an aggregated incident (doc 06 §3).</summary>
public enum IncidentState : int
{
    Open = 0,
    Acknowledged = 1,
    Mitigated = 2,
    Resolved = 3,
    Reopened = 4
}

/// <summary>
/// Resolution codes required when an incident is moved to <see cref="IncidentState.Resolved"/>
/// (doc 06 §3). Notes are redacted to 0–500 safe characters and must never contain secrets.
/// </summary>
public enum IncidentResolutionCode : int
{
    Remediated = 0,
    SourceDisabled = 1,
    CredentialRotated = 2,
    FalsePositive = 3,
    AcceptedRisk = 4,
    Other = 5
}
