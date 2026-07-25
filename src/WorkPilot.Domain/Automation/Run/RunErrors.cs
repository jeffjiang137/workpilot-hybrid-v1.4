using System.Collections.Generic;
using System.Collections.Immutable;
using WorkPilot.Contracts.Primitives;

namespace WorkPilot.Domain.Automation.Run;

/// <summary>
/// Versioned error catalog for the Run/Execution feature (RUN-* codes). Registered globally so the
/// catalog enforces cross-feature code uniqueness (AI dev rule §13).
/// </summary>
public sealed class RunErrors : FeatureErrorCatalog
{
    public override string Feature => "Run";

    public static readonly ErrorDefinition NotFound = new("RUN_NOT_FOUND", ErrorCategory.Resource, "Run.NotFound", false);
    public static readonly ErrorDefinition SnapshotJsonEmpty = new("RUN_SNAPSHOT_JSON_EMPTY", ErrorCategory.Validation, "Run.SnapshotJsonEmpty", false);
    public static readonly ErrorDefinition SnapshotCanonical = new("RUN_SNAPSHOT_CANONICAL", ErrorCategory.Validation, "Run.SnapshotCanonical", false);
    public static readonly ErrorDefinition InvalidRevocationEpoch = new("RUN_REVOCATION_EPOCH", ErrorCategory.Validation, "Run.InvalidRevocationEpoch", false);
    public static readonly ErrorDefinition InvalidPriority = new("RUN_PRIORITY", ErrorCategory.Validation, "Run.InvalidPriority", false);
    public static readonly ErrorDefinition OccurrenceTriggerIdEmpty = new("RUN_OCC_TRIGGER_ID", ErrorCategory.Validation, "Run.OccurrenceTriggerIdEmpty", false);
    public static readonly ErrorDefinition OccurrenceDedupe = new("RUN_OCC_DEDUPE", ErrorCategory.Validation, "Run.OccurrenceDedupe", false);
    public static readonly ErrorDefinition OccurrenceTriggerJsonEmpty = new("RUN_OCC_TRIGGER_JSON", ErrorCategory.Validation, "Run.OccurrenceTriggerJsonEmpty", false);
    public static readonly ErrorDefinition InvalidMissedCount = new("RUN_OCC_MISSED", ErrorCategory.Validation, "Run.InvalidMissedCount", false);
    public static readonly ErrorDefinition StepNodeIdEmpty = new("RUN_STEP_NODE_ID", ErrorCategory.Validation, "Run.StepNodeIdEmpty", false);
    public static readonly ErrorDefinition StepNodeKindEmpty = new("RUN_STEP_NODE_KIND", ErrorCategory.Validation, "Run.StepNodeKindEmpty", false);
    public static readonly ErrorDefinition StepIdempotencyEmpty = new("RUN_STEP_IDEMPOTENCY", ErrorCategory.Validation, "Run.StepIdempotencyEmpty", false);
    public static readonly ErrorDefinition StepInputDigestEmpty = new("RUN_STEP_INPUT_DIGEST", ErrorCategory.Validation, "Run.StepInputDigestEmpty", false);
    public static readonly ErrorDefinition StepLogicalExecution = new("RUN_STEP_LOGICAL_EXEC", ErrorCategory.Validation, "Run.StepLogicalExecution", false);
    public static readonly ErrorDefinition StepAttempt = new("RUN_STEP_ATTEMPT", ErrorCategory.Validation, "Run.StepAttempt", false);
    public static readonly ErrorDefinition EventKindEmpty = new("RUN_EVENT_KIND", ErrorCategory.Validation, "Run.EventKindEmpty", false);
    public static readonly ErrorDefinition EventCodeEmpty = new("RUN_EVENT_CODE", ErrorCategory.Validation, "Run.EventCodeEmpty", false);
    public static readonly ErrorDefinition EventMessageKeyEmpty = new("RUN_EVENT_MSG_KEY", ErrorCategory.Validation, "Run.EventMessageKeyEmpty", false);
    public static readonly ErrorDefinition EventPropertiesEmpty = new("RUN_EVENT_PROPS", ErrorCategory.Validation, "Run.EventPropertiesEmpty", false);
    public static readonly ErrorDefinition EventCorrelationEmpty = new("RUN_EVENT_CORRELATION", ErrorCategory.Validation, "Run.EventCorrelationEmpty", false);
    public static readonly ErrorDefinition ConcurrencyConflict = new("RUN_CONCURRENCY", ErrorCategory.Conflict, "Run.ConcurrencyConflict", false);
    public static readonly ErrorDefinition AlreadyClaimed = new("RUN_ALREADY_CLAIMED", ErrorCategory.Conflict, "Run.AlreadyClaimed", false);
    public static readonly ErrorDefinition AlreadyTerminal = new("RUN_ALREADY_TERMINAL", ErrorCategory.Conflict, "Run.AlreadyTerminal", false);
    public static readonly ErrorDefinition IllegalRunTransition = new("RUN_ILLEGAL_TRANSITION", ErrorCategory.Conflict, "Run.IllegalTransition", false);
    public static readonly ErrorDefinition StateTransitionRejected = new("RUN_STATE_REJECTED", ErrorCategory.Conflict, "Run.StateTransitionRejected", false);
    public static readonly ErrorDefinition StepStateRejected = new("RUN_STEP_STATE_REJECTED", ErrorCategory.Conflict, "Run.StepStateRejected", false);
    public static readonly ErrorDefinition WorkflowEmpty = new("RUN_WORKFLOW_EMPTY", ErrorCategory.Validation, "Run.WorkflowEmpty", false);
    public static readonly ErrorDefinition EntryNodeMissing = new("RUN_ENTRY_MISSING", ErrorCategory.Validation, "Run.EntryNodeMissing", false);
    public static readonly ErrorDefinition VariableBindingFailed = new("RUN_VAR_BINDING", ErrorCategory.Validation, "Run.VariableBindingFailed", false);
    public static readonly ErrorDefinition ConditionEvaluation = new("RUN_CONDITION_EVAL", ErrorCategory.Validation, "Run.ConditionEvaluationError", false);
    public static readonly ErrorDefinition RunWallClockBudgetExceeded = new("RUN_BUDGET_WALLCLOCK", ErrorCategory.Resource, "Run.WallClockBudgetExceeded", false);
    public static readonly ErrorDefinition ModelTurnBudgetExceeded = new("RUN_BUDGET_MODEL_TURN", ErrorCategory.Resource, "Run.ModelTurnBudgetExceeded", false);
    public static readonly ErrorDefinition CapabilityCallBudgetExceeded = new("RUN_BUDGET_CAPABILITY", ErrorCategory.Resource, "Run.CapabilityCallBudgetExceeded", false);
    public static readonly ErrorDefinition ResultBudgetExceeded = new("RUN_BUDGET_RESULT", ErrorCategory.Resource, "Run.ResultBudgetExceeded", false);
    public static readonly ErrorDefinition StepNodeNotFound = new("RUN_STEP_NODE_NOT_FOUND", ErrorCategory.Validation, "Run.StepNodeNotFound", false);

    // T11: Agent / Delay / Notification executors (RUN-004 / RUN-008)
    public static readonly ErrorDefinition NodeKindNotSupported = new("RUN_NODE_KIND_NOT_SUPPORTED", ErrorCategory.Validation, "Run.NodeKindNotSupported", false);
    public static readonly ErrorDefinition DelayInvalid = new("RUN_DELAY_INVALID", ErrorCategory.Validation, "Run.DelayInvalid", false);
    public static readonly ErrorDefinition DelayClockInvalid = new("RUN_DELAY_CLOCK_INVALID", ErrorCategory.Validation, "Run.DelayClockInvalid", false);
    public static readonly ErrorDefinition AgentInstructionMissing = new("RUN_AGENT_INSTRUCTION", ErrorCategory.Validation, "Run.AgentInstructionMissing", false);
    public static readonly ErrorDefinition AgentBackendFailed = new("RUN_AGENT_BACKEND", ErrorCategory.Resource, "Run.AgentBackendFailed", false);
    public static readonly ErrorDefinition NotificationRenderFailed = new("RUN_NOTIFICATION_RENDER", ErrorCategory.Validation, "Run.NotificationRenderFailed", false);
    public static readonly ErrorDefinition NotificationDeliveryFailed = new("RUN_NOTIFICATION_FAILED", ErrorCategory.Resource, "Run.NotificationDeliveryFailed", false);

    // T12: Capability executor + Native single-use Permit (RUN-004 / PER-006 / PER-007)
    public static readonly ErrorDefinition CapabilityNotFound = new("RUN_CAPABILITY_NOT_FOUND", ErrorCategory.Policy, "Run.CapabilityNotFound", false);
    public static readonly ErrorDefinition PermitInvalid = new("RUN_PERMIT_INVALID", ErrorCategory.Policy, "Run.PermitInvalid", false);
    public static readonly ErrorDefinition PermitForged = new("RUN_PERMIT_FORGED", ErrorCategory.Policy, "Run.PermitForged", false);
    public static readonly ErrorDefinition PermitAlreadyConsumed = new("RUN_PERMIT_CONSUMED", ErrorCategory.Policy, "Run.PermitAlreadyConsumed", false);
    public static readonly ErrorDefinition PermitExpired = new("RUN_PERMIT_EXPIRED", ErrorCategory.Policy, "Run.PermitExpired", false);
    public static readonly ErrorDefinition PermitEpochChanged = new("RUN_PERMIT_EPOCH", ErrorCategory.Policy, "Run.PermitEpochChanged", false);
    public static readonly ErrorDefinition PermitLeaseLost = new("RUN_PERMIT_LEASE", ErrorCategory.Conflict, "Run.PermitLeaseLost", false);
    public static readonly ErrorDefinition PermitCancelled = new("RUN_PERMIT_CANCELLED", ErrorCategory.Cancelled, "Run.PermitCancelled", false);
    public static readonly ErrorDefinition PermitIssueFailed = new("RUN_PERMIT_ISSUE", ErrorCategory.Policy, "Run.PermitIssueFailed", false);
    public static readonly ErrorDefinition CapabilityInvokeFailed = new("RUN_CAPABILITY_INVOKE", ErrorCategory.Conflict, "Run.CapabilityInvokeFailed", false);

    // T13: Retry policy (RUN-004) + transient retryable markers (doc 04 §10)
    public static readonly ErrorDefinition RetryPolicyInvalid = new("RUN_RETRY_POLICY", ErrorCategory.Validation, "Run.RetryPolicyInvalid", false);
    public static readonly ErrorDefinition TransientDns = new("RUN_TRANSIENT_DNS", ErrorCategory.Network, "Run.TransientDns", false);
    public static readonly ErrorDefinition TransientConnection = new("RUN_TRANSIENT_CONNECTION", ErrorCategory.Network, "Run.TransientConnection", false);
    public static readonly ErrorDefinition TransientHttp408 = new("RUN_TRANSIENT_HTTP_408", ErrorCategory.Protocol, "Run.TransientHttp408", false);
    public static readonly ErrorDefinition TransientHttp429 = new("RUN_TRANSIENT_HTTP_429", ErrorCategory.Protocol, "Run.TransientHttp429", false);
    public static readonly ErrorDefinition TransientHttp5xx = new("RUN_TRANSIENT_HTTP_5XX", ErrorCategory.Protocol, "Run.TransientHttp5xx", false);
    public static readonly ErrorDefinition TransientProtocolBusy = new("RUN_TRANSIENT_PROTOCOL_BUSY", ErrorCategory.Protocol, "Run.TransientProtocolBusy", false);
    public static readonly ErrorDefinition TransientSqliteBusy = new("RUN_TRANSIENT_SQLITE_BUSY", ErrorCategory.Database, "Run.TransientSqliteBusy", false);

    // T13: Crash recovery (doc 04 §9/§13)
    public static readonly ErrorDefinition RecoveryRepeatedCrash = new("RUN_RECOVERY_REPEATED_CRASH", ErrorCategory.Internal, "Run.RecoveryRepeatedCrash", false);
    public static readonly ErrorDefinition RecoveryOutcomeUnknown = new("RUN_RECOVERY_OUTCOME_UNKNOWN", ErrorCategory.Internal, "Run.RecoveryOutcomeUnknown", false);

    // T13: Approval coordinator (PER-005)
    public static readonly ErrorDefinition ApprovalNotFound = new("RUN_APPROVAL_NOT_FOUND", ErrorCategory.Resource, "Run.ApprovalNotFound", false);
    public static readonly ErrorDefinition ApprovalAlreadyDecided = new("RUN_APPROVAL_ALREADY_DECIDED", ErrorCategory.Conflict, "Run.ApprovalAlreadyDecided", false);
    public static readonly ErrorDefinition ApprovalExpired = new("RUN_APPROVAL_EXPIRED", ErrorCategory.Conflict, "Run.ApprovalExpired", false);
    public static readonly ErrorDefinition ApprovalPreconditionChanged = new("RUN_APPROVAL_PRECONDITION_CHANGED", ErrorCategory.Policy, "Run.ApprovalPreconditionChanged", false);
    public static readonly ErrorDefinition ReceiptConsumed = new("RUN_RECEIPT_CONSUMED", ErrorCategory.Conflict, "Run.ReceiptConsumed", false);
    public static readonly ErrorDefinition ReceiptExpired = new("RUN_RECEIPT_EXPIRED", ErrorCategory.Conflict, "Run.ReceiptExpired", false);
    public static readonly ErrorDefinition ReceiptNotFound = new("RUN_RECEIPT_NOT_FOUND", ErrorCategory.Resource, "Run.ReceiptNotFound", false);

    // T14: Run Event contract + redaction (LOG-002/003/004/007)
    public static readonly ErrorDefinition LoggingContractViolation = new("RUN_EVENT_CONTRACT_VIOLATION", ErrorCategory.Validation, "Run.EventContractViolation", false);
    public static readonly ErrorDefinition RedactionFailure = new("RUN_REDACTION_FAILURE", ErrorCategory.Policy, "Run.RedactionFailure", false);
    public static readonly ErrorDefinition RedactionCanaryDetected = new("RUN_REDACTION_CANARY", ErrorCategory.Policy, "Run.RedactionCanary", false);

    public override IReadOnlyList<ErrorDefinition> Definitions => new[]
    {
        NotFound, SnapshotJsonEmpty, SnapshotCanonical, InvalidRevocationEpoch, InvalidPriority,
        OccurrenceTriggerIdEmpty, OccurrenceDedupe, OccurrenceTriggerJsonEmpty, InvalidMissedCount,
        StepNodeIdEmpty, StepNodeKindEmpty, StepIdempotencyEmpty, StepInputDigestEmpty,
        StepLogicalExecution, StepAttempt, EventKindEmpty, EventCodeEmpty, EventMessageKeyEmpty,
        EventPropertiesEmpty, EventCorrelationEmpty, ConcurrencyConflict, AlreadyClaimed, AlreadyTerminal,
        IllegalRunTransition, StateTransitionRejected, StepStateRejected, WorkflowEmpty, EntryNodeMissing,
        VariableBindingFailed, ConditionEvaluation, RunWallClockBudgetExceeded, ModelTurnBudgetExceeded,
        CapabilityCallBudgetExceeded, ResultBudgetExceeded, StepNodeNotFound,
        NodeKindNotSupported, DelayInvalid, DelayClockInvalid, AgentInstructionMissing,
        AgentBackendFailed, NotificationRenderFailed, NotificationDeliveryFailed,
        CapabilityNotFound, PermitInvalid, PermitForged, PermitAlreadyConsumed, PermitExpired,
        PermitEpochChanged, PermitLeaseLost, PermitCancelled, PermitIssueFailed, CapabilityInvokeFailed,
        RetryPolicyInvalid, TransientDns, TransientConnection, TransientHttp408, TransientHttp429,
        TransientHttp5xx, TransientProtocolBusy, TransientSqliteBusy,
        RecoveryRepeatedCrash, RecoveryOutcomeUnknown,
        ApprovalNotFound, ApprovalAlreadyDecided, ApprovalExpired, ApprovalPreconditionChanged,
        ReceiptConsumed, ReceiptExpired, ReceiptNotFound,

        // T14: Run Event contract + redaction (LOG-002/003/004/007)
        LoggingContractViolation, RedactionFailure, RedactionCanaryDetected
    };

    /// <summary>Retryable transient error codes (doc 04 §10). Classification lives in <see cref="RetryClassifier"/>.</summary>
    public static readonly IReadOnlySet<string> RetryableTransientCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        TransientDns.Code, TransientConnection.Code, TransientHttp408.Code, TransientHttp429.Code,
        TransientHttp5xx.Code, TransientProtocolBusy.Code, TransientSqliteBusy.Code
    };

    public static bool IsRetryableTransientCode(string? code)
        => code is not null && RetryableTransientCodes.Contains(code);

    public static readonly RunErrors Instance = new();

    static RunErrors() => ErrorCatalog.Register(Instance);

    public static AppError NotFoundError() => Instance.Error("RUN_NOT_FOUND");
    public static AppError SnapshotJsonEmptyError(string which)
        => Instance.Error("RUN_SNAPSHOT_JSON_EMPTY", new Dictionary<string, string> { ["field"] = which });
    public static AppError SnapshotCanonicalError() => Instance.Error("RUN_SNAPSHOT_CANONICAL");
    public static AppError InvalidRevocationEpochError() => Instance.Error("RUN_REVOCATION_EPOCH");
    public static AppError InvalidPriorityError() => Instance.Error("RUN_PRIORITY");
    public static AppError OccurrenceTriggerIdEmptyError() => Instance.Error("RUN_OCC_TRIGGER_ID");
    public static AppError OccurrenceDedupeError() => Instance.Error("RUN_OCC_DEDUPE");
    public static AppError OccurrenceTriggerJsonEmptyError() => Instance.Error("RUN_OCC_TRIGGER_JSON");
    public static AppError InvalidMissedCountError() => Instance.Error("RUN_OCC_MISSED");
    public static AppError StepNodeIdEmptyError() => Instance.Error("RUN_STEP_NODE_ID");
    public static AppError StepNodeKindEmptyError() => Instance.Error("RUN_STEP_NODE_KIND");
    public static AppError StepIdempotencyEmptyError() => Instance.Error("RUN_STEP_IDEMPOTENCY");
    public static AppError StepInputDigestEmptyError() => Instance.Error("RUN_STEP_INPUT_DIGEST");
    public static AppError StepLogicalExecutionError() => Instance.Error("RUN_STEP_LOGICAL_EXEC");
    public static AppError StepAttemptError() => Instance.Error("RUN_STEP_ATTEMPT");
    public static AppError EventKindEmptyError() => Instance.Error("RUN_EVENT_KIND");
    public static AppError EventCodeEmptyError() => Instance.Error("RUN_EVENT_CODE");
    public static AppError EventMessageKeyEmptyError() => Instance.Error("RUN_EVENT_MSG_KEY");
    public static AppError EventPropertiesEmptyError() => Instance.Error("RUN_EVENT_PROPS");
    public static AppError EventCorrelationEmptyError() => Instance.Error("RUN_EVENT_CORRELATION");
    public static AppError ConcurrencyConflictError() => Instance.Error("RUN_CONCURRENCY");
    public static AppError AlreadyClaimedError() => Instance.Error("RUN_ALREADY_CLAIMED");
    public static AppError AlreadyTerminalError() => Instance.Error("RUN_ALREADY_TERMINAL");
    public static AppError IllegalRunTransitionError(RunStatus from, RunStatus to)
        => Instance.Error("RUN_ILLEGAL_TRANSITION", new Dictionary<string, string>
            { ["from"] = from.ToString(), ["to"] = to.ToString() });
    public static AppError StateTransitionRejectedError(RunStatus from, RunStatus to)
        => Instance.Error("RUN_STATE_REJECTED", new Dictionary<string, string>
            { ["from"] = from.ToString(), ["to"] = to.ToString() });
    public static AppError StepStateRejectedError(StepRunStatus from, StepRunStatus to)
        => Instance.Error("RUN_STEP_STATE_REJECTED", new Dictionary<string, string>
            { ["from"] = from.ToString(), ["to"] = to.ToString() });
    public static AppError WorkflowEmptyError() => Instance.Error("RUN_WORKFLOW_EMPTY");
    public static AppError EntryNodeMissingError(string entryNodeId)
        => Instance.Error("RUN_ENTRY_MISSING", new Dictionary<string, string> { ["entry_node_id"] = entryNodeId });
    public static AppError VariableBindingFailedError(string nodeId, string reference)
        => Instance.Error("RUN_VAR_BINDING", new Dictionary<string, string> { ["node_id"] = nodeId, ["reference"] = reference });
    public static AppError ConditionEvaluationError(string nodeId, string detail)
        => Instance.Error("RUN_CONDITION_EVAL", new Dictionary<string, string> { ["node_id"] = nodeId, ["detail"] = detail });
    public static AppError RunWallClockBudgetExceededError(string nodeId)
        => Instance.Error("RUN_BUDGET_WALLCLOCK", new Dictionary<string, string> { ["node_id"] = nodeId });
    public static AppError ModelTurnBudgetExceededError(string nodeId)
        => Instance.Error("RUN_BUDGET_MODEL_TURN", new Dictionary<string, string> { ["node_id"] = nodeId });
    public static AppError CapabilityCallBudgetExceededError(string nodeId)
        => Instance.Error("RUN_BUDGET_CAPABILITY", new Dictionary<string, string> { ["node_id"] = nodeId });
    public static AppError ResultBudgetExceededError(string nodeId)
        => Instance.Error("RUN_BUDGET_RESULT", new Dictionary<string, string> { ["node_id"] = nodeId });
    public static AppError StepNodeNotFoundError(string nodeId)
        => Instance.Error("RUN_STEP_NODE_NOT_FOUND", new Dictionary<string, string> { ["node_id"] = nodeId });

    // T11 factory methods
    public static AppError NodeKindNotSupportedError(string kind)
        => Instance.Error("RUN_NODE_KIND_NOT_SUPPORTED", new Dictionary<string, string> { ["kind"] = kind });
    public static AppError DelayInvalidError(string nodeId, string detail)
        => Instance.Error("RUN_DELAY_INVALID", new Dictionary<string, string> { ["node_id"] = nodeId, ["detail"] = detail });
    public static AppError DelayClockInvalidError(string nodeId)
        => Instance.Error("RUN_DELAY_CLOCK_INVALID", new Dictionary<string, string> { ["node_id"] = nodeId });
    public static AppError AgentInstructionMissingError(string nodeId)
        => Instance.Error("RUN_AGENT_INSTRUCTION", new Dictionary<string, string> { ["node_id"] = nodeId });
    public static AppError AgentBackendFailedError(string nodeId, string? detail = null)
        => Instance.Error("RUN_AGENT_BACKEND", detail is null
            ? new Dictionary<string, string> { ["node_id"] = nodeId }
            : new Dictionary<string, string> { ["node_id"] = nodeId, ["detail"] = detail });
    public static AppError NotificationRenderFailedError(string nodeId, string reference)
        => Instance.Error("RUN_NOTIFICATION_RENDER", new Dictionary<string, string> { ["node_id"] = nodeId, ["reference"] = reference });
    public static AppError NotificationDeliveryFailedError(string nodeId)
        => Instance.Error("RUN_NOTIFICATION_FAILED", new Dictionary<string, string> { ["node_id"] = nodeId });

    // T12 factory methods
    public static AppError CapabilityNotFoundError(string nodeId, string stableId)
        => Instance.Error("RUN_CAPABILITY_NOT_FOUND", new Dictionary<string, string> { ["node_id"] = nodeId, ["stable_id"] = stableId });
    public static AppError PermitInvalidError() => Instance.Error("RUN_PERMIT_INVALID");
    public static AppError PermitForgedError() => Instance.Error("RUN_PERMIT_FORGED");
    public static AppError PermitAlreadyConsumedError() => Instance.Error("RUN_PERMIT_CONSUMED");
    public static AppError PermitExpiredError() => Instance.Error("RUN_PERMIT_EXPIRED");
    public static AppError PermitEpochChangedError() => Instance.Error("RUN_PERMIT_EPOCH");
    public static AppError PermitLeaseLostError() => Instance.Error("RUN_PERMIT_LEASE");
    public static AppError PermitCancelledError() => Instance.Error("RUN_PERMIT_CANCELLED");
    public static AppError PermitIssueFailedError(string nodeId, string detail)
        => Instance.Error("RUN_PERMIT_ISSUE", new Dictionary<string, string> { ["node_id"] = nodeId, ["detail"] = detail });
    public static AppError CapabilityInvokeFailedError(string nodeId, string? detail = null)
        => Instance.Error("RUN_CAPABILITY_INVOKE", detail is null
            ? new Dictionary<string, string> { ["node_id"] = nodeId }
            : new Dictionary<string, string> { ["node_id"] = nodeId, ["detail"] = detail });

    // T13 factory methods
    public static AppError RetryPolicyInvalidError(string detail)
        => Instance.Error("RUN_RETRY_POLICY", new Dictionary<string, string> { ["detail"] = detail });
    public static AppError TransientDnsError(string nodeId)
        => Instance.Error("RUN_TRANSIENT_DNS", new Dictionary<string, string> { ["node_id"] = nodeId });
    public static AppError TransientConnectionError(string nodeId)
        => Instance.Error("RUN_TRANSIENT_CONNECTION", new Dictionary<string, string> { ["node_id"] = nodeId });
    public static AppError TransientHttp408Error(string nodeId)
        => Instance.Error("RUN_TRANSIENT_HTTP_408", new Dictionary<string, string> { ["node_id"] = nodeId });
    public static AppError TransientHttp429Error(string nodeId)
        => Instance.Error("RUN_TRANSIENT_HTTP_429", new Dictionary<string, string> { ["node_id"] = nodeId });
    public static AppError TransientHttp5xxError(string nodeId)
        => Instance.Error("RUN_TRANSIENT_HTTP_5XX", new Dictionary<string, string> { ["node_id"] = nodeId });
    public static AppError TransientProtocolBusyError(string nodeId)
        => Instance.Error("RUN_TRANSIENT_PROTOCOL_BUSY", new Dictionary<string, string> { ["node_id"] = nodeId });
    public static AppError TransientSqliteBusyError(string nodeId)
        => Instance.Error("RUN_TRANSIENT_SQLITE_BUSY", new Dictionary<string, string> { ["node_id"] = nodeId });
    public static AppError RecoveryRepeatedCrashError(string runId)
        => Instance.Error("RUN_RECOVERY_REPEATED_CRASH", new Dictionary<string, string> { ["run_id"] = runId });
    public static AppError RecoveryOutcomeUnknownError(string nodeId)
        => Instance.Error("RUN_RECOVERY_OUTCOME_UNKNOWN", new Dictionary<string, string> { ["node_id"] = nodeId });
    public static AppError ApprovalNotFoundError(string approvalId)
        => Instance.Error("RUN_APPROVAL_NOT_FOUND", new Dictionary<string, string> { ["approval_id"] = approvalId });
    public static AppError ApprovalAlreadyDecidedError(string approvalId, string status)
        => Instance.Error("RUN_APPROVAL_ALREADY_DECIDED", new Dictionary<string, string> { ["approval_id"] = approvalId, ["status"] = status });
    public static AppError ApprovalExpiredError(string approvalId)
        => Instance.Error("RUN_APPROVAL_EXPIRED", new Dictionary<string, string> { ["approval_id"] = approvalId });
    public static AppError ApprovalPreconditionChangedError(string approvalId, string reason)
        => Instance.Error("RUN_APPROVAL_PRECONDITION_CHANGED", new Dictionary<string, string> { ["approval_id"] = approvalId, ["reason"] = reason });
    public static AppError ReceiptConsumedError(string receiptId)
        => Instance.Error("RUN_RECEIPT_CONSUMED", new Dictionary<string, string> { ["receipt_id"] = receiptId });
    public static AppError ReceiptExpiredError(string receiptId)
        => Instance.Error("RUN_RECEIPT_EXPIRED", new Dictionary<string, string> { ["receipt_id"] = receiptId });
    public static AppError ReceiptNotFoundError(string receiptId)
        => Instance.Error("RUN_RECEIPT_NOT_FOUND", new Dictionary<string, string> { ["receipt_id"] = receiptId });

    // T14 factory methods (LOG-002/003/004/007)
    public static AppError LoggingContractViolationError(string kind, string detail)
        => Instance.Error("RUN_EVENT_CONTRACT_VIOLATION", new Dictionary<string, string> { ["kind"] = kind, ["detail"] = detail });
    public static AppError RedactionFailureError()
        => Instance.Error("RUN_REDACTION_FAILURE");
    public static AppError RedactionCanaryDetectedError()
        => Instance.Error("RUN_REDACTION_CANARY");
}
