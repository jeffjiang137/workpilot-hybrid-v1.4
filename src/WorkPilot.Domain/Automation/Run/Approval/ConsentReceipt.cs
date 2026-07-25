using System;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;

namespace WorkPilot.Domain.Automation.Run.Approval;

/// <summary>Lifecycle of a one-time consent receipt (doc 07 §9).</summary>
public enum ReceiptStatus
{
    Issued,
    Consumed,
    Invalidated,
    Expired
}

/// <summary>
/// A one-time execution credential issued when an approval is granted (doc 07 §9, PER-005). Bound to
/// run/step/attempt/capability/schema/argument-digest/scope/risk/policy-trace/epoch with a 5-minute
/// execution window. Single-use: consumed on execution success/failure, invalidated on cancel/expiry.
/// Cannot be serialized to a model or adapter (the Native Core issues the in-memory ExecutionPermit).
/// </summary>
public sealed record ConsentReceipt(
    string Id,
    string ApprovalRequestId,
    RunId RunId,
    StepRunId StepId,
    int Attempt,
    string SourceKind,
    string SourceId,
    string CapabilityStableId,
    string SchemaSha256,
    string ArgumentDigest,
    string ScopeDigest,
    int RiskLevel,
    string PolicyTraceSha256,
    long Epoch,
    ReceiptStatus Status,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? ConsumedAtUtc,
    DateTimeOffset CreatedAtUtc,
    int RowVersion)
{
    public static ConsentReceipt Issue(
        string id,
        ApprovalRequest approval,
        int attempt,
        DateTimeOffset now)
    {
        if (approval.Status != ApprovalStatus.Pending && approval.Status != ApprovalStatus.Approved)
            throw new DomainException(RunErrors.ApprovalPreconditionChangedError(approval.Id, "not_issuable"));
        var expires = now.AddMinutes(Limits.V1_5.ConsentReceiptExecutionWindowMinutes);
        return new ConsentReceipt(id, approval.Id, approval.RunId, approval.StepId, attempt,
            approval.SourceKind, approval.SourceId, approval.CapabilityStableId, approval.SchemaSha256,
            approval.ArgumentDigest, approval.ScopeDigest, approval.RiskLevel, approval.PolicyTraceSha256,
            approval.Epoch, ReceiptStatus.Issued, expires, null, now, 1);
    }

    /// <summary>True while Issued and past the execution window.</summary>
    public bool IsExpired(DateTimeOffset now) => Status == ReceiptStatus.Issued && ExpiresAtUtc < now;

    /// <summary>Single-use consumption. Fails if already consumed, invalidated, or expired.</summary>
    public Result<ConsentReceipt> Consume(DateTimeOffset now)
    {
        if (Status != ReceiptStatus.Issued)
            return Result<ConsentReceipt>.Fail(Status == ReceiptStatus.Consumed
                ? RunErrors.ReceiptConsumedError(Id)
                : RunErrors.ReceiptExpiredError(Id));
        if (ExpiresAtUtc < now)
            return Result<ConsentReceipt>.Fail(RunErrors.ReceiptExpiredError(Id));
        return Result<ConsentReceipt>.Ok(this with { Status = ReceiptStatus.Consumed, ConsumedAtUtc = now, RowVersion = RowVersion + 1 });
    }

    /// <summary>Invalidates the receipt (e.g., run cancelled before use).</summary>
    public ConsentReceipt Invalidate(DateTimeOffset now)
        => this with { Status = ReceiptStatus.Invalidated, ConsumedAtUtc = now, RowVersion = RowVersion + 1 };
}
