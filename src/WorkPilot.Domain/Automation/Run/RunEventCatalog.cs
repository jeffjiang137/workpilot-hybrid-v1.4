namespace WorkPilot.Domain.Automation.Run;

/// <summary>Stable run-event <c>kind</c> strings for T13 recovery / approval events (LOG-002).</summary>
public static class RunEventKinds
{
    public const string Recovery = "run.recovery";
    public const string Approval = "run.approval";
    public const string Receipt = "run.receipt";
}

/// <summary>Stable run-event <c>code</c> strings emitted by the T13 recovery / approval coordinators.</summary>
public static class RunEventCodes
{
    public const string RecoveryRequeued = "RECOVERY_REQUEUED";
    public const string RecoveryIdempotentRequeue = "RECOVERY_IDEMPOTENT_REQUEUED";
    public const string RecoveryNeedsReview = "RECOVERY_NEEDS_REVIEW";
    public const string RecoveryCompleted = "RECOVERY_COMPLETED";
    public const string RecoveryRepeatedCrash = "RECOVERY_REPEATED_CRASH";
    public const string ApprovalCreated = "APPROVAL_CREATED";
    public const string ApprovalApproved = "APPROVAL_APPROVED";
    public const string ApprovalExpired = "APPROVAL_EXPIRED";
    public const string ApprovalPreconditionChanged = "APPROVAL_PRECONDITION_CHANGED";
    public const string ReceiptIssued = "RECEIPT_ISSUED";
    public const string ReceiptConsumed = "RECEIPT_CONSUMED";
}
