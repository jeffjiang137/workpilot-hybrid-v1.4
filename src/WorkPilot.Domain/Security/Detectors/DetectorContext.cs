using System.Collections.Generic;
using WorkPilot.Contracts.Primitives.Ids;

namespace WorkPilot.Domain.Security.Detectors;

/// <summary>
/// Thresholds from doc 06 §4, kept in one place so rules stay declarative and testable.
/// </summary>
public static class DetectorThresholds
{
    public const int AuthFailureCount = 5;
    public static readonly System.TimeSpan AuthFailureWindow = System.TimeSpan.FromMinutes(5);

    public const int McpProtocolViolationCount = 3;
    public static readonly System.TimeSpan McpProtocolWindow = System.TimeSpan.FromMinutes(10);

    public const int WorkerCrashRecoveryCount = 3; // strictly greater than 3 → 4+
    public static readonly System.TimeSpan WorkerCrashWindow = System.TimeSpan.FromHours(1);

    public const int PolicyDenialCount = 10;
    public static readonly System.TimeSpan PolicyDenialWindow = System.TimeSpan.FromMinutes(10);

    public const int QueueDepthLimit = 800;
    public static readonly System.TimeSpan QueueOldestWaitLimit = System.TimeSpan.FromMinutes(30);

    public const long DiskFreeMiBLimit = 200;

    public const int ApprovalRejectionCount = 5;
    public static readonly System.TimeSpan ApprovalRejectionWindow = System.TimeSpan.FromHours(24);
}

/// <summary>Input samples for the 16 fixed detectors (doc 06 §4). Display-name-free.</summary>
public sealed record AuthFailureSample(SourceReference Source, int Count);
public sealed record McpSchemaChange(SourceReference Source, string CapabilityStableId, string OldSchemaSha256, string NewSchemaSha256);
public sealed record McpProtocolViolation(SourceReference Source, int Count);
public sealed record ExecutableHashChange(SourceReference Source, string ExecutableId, string OldHash, string NewHash);
public sealed record OAuthMismatch(SourceReference Source, string Detail);
public sealed record DpapiFailure(SourceReference Source, string Detail);
public sealed record RedactionCanaryHit(string CanaryToken, SourceReference? Source);
public sealed record AuditIntegrityFailureSignal(string Detail);
public sealed record PolicyDenialSample(AutomationId AutomationId, SourceReference? Source, int Count);
public sealed record WorkerCrashRecovery(SourceReference? Source, AutomationId? AutomationId, int Count);
public sealed record QueueBackpressure(int Depth, System.TimeSpan OldestWait);
public sealed record DiskSpaceLow(long FreeMiB);
public sealed record CapabilityNoPermit(SourceReference Source, string CapabilityStableId, string SchemaSha256);
public sealed record LeaseLostSendAttempt(SourceReference Source, RunId RunId, string CapabilityStableId);
public sealed record OutcomeUnknownWrite(SourceReference Source, RunId RunId, string CapabilityStableId);
public sealed record ApprovalRejectionBurst(AutomationId AutomationId, int Count);

/// <summary>
/// Snapshot of observable signals fed to <see cref="IDetectorRule"/> instances each detection pass.
/// Built by the host/adapter telemetry layer; contains only safe, display-name-free data.
/// </summary>
public sealed record DetectorContext(
    System.DateTimeOffset ObservedAtUtc,
    IReadOnlyList<AuthFailureSample> AuthFailures,
    IReadOnlyList<McpSchemaChange> McpSchemaChanges,
    IReadOnlyList<McpProtocolViolation> McpProtocolViolations,
    IReadOnlyList<ExecutableHashChange> ExecutableHashChanges,
    IReadOnlyList<OAuthMismatch> OAuthMismatches,
    IReadOnlyList<DpapiFailure> DpapiFailures,
    IReadOnlyList<RedactionCanaryHit> RedactionCanaryHits,
    IReadOnlyList<AuditIntegrityFailureSignal> AuditIntegrityFailures,
    IReadOnlyList<PolicyDenialSample> PolicyDenials,
    IReadOnlyList<WorkerCrashRecovery> WorkerCrashRecoveries,
    IReadOnlyList<QueueBackpressure> QueueBackpressures,
    IReadOnlyList<DiskSpaceLow> DiskSpaceLows,
    IReadOnlyList<CapabilityNoPermit> CapabilityNoPermits,
    IReadOnlyList<LeaseLostSendAttempt> LeaseLostSendAttempts,
    IReadOnlyList<OutcomeUnknownWrite> OutcomeUnknownWrites,
    IReadOnlyList<ApprovalRejectionBurst> ApprovalRejectionBursts);

/// <summary>Kind of automatic remediation a detector requests (doc 06 §4). Idempotent by <see cref="DetectorAction.ActionId"/>.</summary>
public enum DetectorActionKind : int
{
    DisableNewCalls = 0,
    StaleGrant = 1,
    StopAndDisableServer = 2,
    DisableSource = 3,
    PauseAutomation = 4,
    DegradeHostHealth = 5,
    StopMaterialization = 6,
    StopNewRuns = 7,
    RejectAndDisableCallPath = 8,
    StopWorker = 9,
    SuggestPause = 10,
    ClearTransientState = 11,
    DiscardEventBody = 12
}

/// <summary>
/// A deterministic, idempotent remediation action. <see cref="ActionId"/> is stable for the same
/// (detector, target) so repeated passes never re-apply it (doc 06 §4 "动作必须幂等").
/// </summary>
public sealed record DetectorAction(string ActionId, DetectorActionKind Kind, string TargetKind, string TargetId);

/// <summary>Version of the detector rule set; stamped on every emitted event.</summary>
public static class DetectorConstants
{
    public const string DetectorVersion = "1.0.0";
}
