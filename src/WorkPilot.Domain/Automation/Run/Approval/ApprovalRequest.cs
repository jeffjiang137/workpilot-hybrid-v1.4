using System;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;

namespace WorkPilot.Domain.Automation.Run.Approval;

/// <summary>Lifecycle of an approval request (doc 04 §11, PER-005).</summary>
public enum ApprovalStatus
{
    Pending,
    Approved,
    Denied,
    Expired,
    Invalidated
}

/// <summary>
/// A High one-time approval request (PER-005). Frozen at creation with run/step/capability/schema/
/// argument digest/scope/risk/policy-trace/epoch and a 10-minute decision window. Approval issues a
/// one-time <see cref="ConsentReceipt"/>; the request itself is consumed (never re-approved).
/// </summary>
public sealed record ApprovalRequest(
    string Id,
    RunId RunId,
    StepRunId StepId,
    ApprovalStatus Status,
    string SourceKind,
    string SourceId,
    string CapabilityStableId,
    string SchemaSha256,
    string ArgumentDigest,
    string ScopeDigest,
    string SafeSummaryJson,
    int RiskLevel,
    string PolicyTraceSha256,
    long Epoch,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? DecidedAtUtc,
    string? DecisionReason,
    DateTimeOffset CreatedAtUtc,
    int RowVersion)
{
    public static ApprovalRequest Create(
        string id,
        RunId runId,
        StepRunId stepId,
        string sourceKind,
        string sourceId,
        string capabilityStableId,
        string schemaSha256,
        string argumentDigest,
        string scopeDigest,
        string safeSummaryJson,
        int riskLevel,
        string policyTraceSha256,
        long epoch,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("id required", nameof(id));
        if (riskLevel < 0 || riskLevel > 3)
            throw new DomainException(RunErrors.RetryPolicyInvalidError("riskLevel must be 0..3"));
        if (string.IsNullOrWhiteSpace(schemaSha256))
            throw new DomainException(RunErrors.RetryPolicyInvalidError("schemaSha256 required"));

        var expires = now.AddMinutes(Limits.V1_5.ApprovalDecisionWindowMinutes);
        return new ApprovalRequest(id, runId, stepId, ApprovalStatus.Pending, sourceKind, sourceId,
            capabilityStableId, schemaSha256, argumentDigest, scopeDigest, safeSummaryJson, riskLevel,
            policyTraceSha256, epoch, expires, null, null, now, 1);
    }

    /// <summary>True while Pending and past the decision window.</summary>
    public bool IsExpired(DateTimeOffset now) => Status == ApprovalStatus.Pending && ExpiresAtUtc < now;

    /// <summary>Marks the request expired (only valid from Pending).</summary>
    public Result<ApprovalRequest> Expire(DateTimeOffset now)
    {
        if (Status != ApprovalStatus.Pending)
            return Result<ApprovalRequest>.Fail(RunErrors.ApprovalAlreadyDecidedError(Id, Status.ToString()));
        if (ExpiresAtUtc >= now)
            return Result<ApprovalRequest>.Fail(RunErrors.ApprovalExpiredError(Id));
        return Result<ApprovalRequest>.Ok(this with { Status = ApprovalStatus.Expired, DecidedAtUtc = now, RowVersion = RowVersion + 1 });
    }

    /// <summary>
    /// Preconditions for approval (doc 04 §11 / PER-005): Pending, not expired, source enabled,
    /// schema unchanged, frozen epoch unchanged. Any failure blocks issuance of a receipt.
    /// </summary>
    public Result PrecheckApprovable(DateTimeOffset now, long currentEpoch, string schemaShaCurrent, bool sourceEnabled)
    {
        if (Status != ApprovalStatus.Pending)
            return Result.Failure(RunErrors.ApprovalAlreadyDecidedError(Id, Status.ToString()));
        if (ExpiresAtUtc < now)
            return Result.Failure(RunErrors.ApprovalExpiredError(Id));
        if (!sourceEnabled)
            return Result.Failure(RunErrors.ApprovalPreconditionChangedError(Id, "source_disabled"));
        if (SchemaSha256 != schemaShaCurrent)
            return Result.Failure(RunErrors.ApprovalPreconditionChangedError(Id, "schema_changed"));
        if (Epoch != currentEpoch)
            return Result.Failure(RunErrors.ApprovalPreconditionChangedError(Id, "epoch_changed"));
        return Result.Success();
    }

    /// <summary>Transitions to Approved (caller must have passed <see cref="PrecheckApprovable"/>).</summary>
    public ApprovalRequest MarkApproved(DateTimeOffset now, string? reason = null)
        => this with { Status = ApprovalStatus.Approved, DecidedAtUtc = now, DecisionReason = reason ?? "approved", RowVersion = RowVersion + 1 };

    /// <summary>Transitions to Denied.</summary>
    public ApprovalRequest MarkDenied(DateTimeOffset now, string reason)
        => this with { Status = ApprovalStatus.Denied, DecidedAtUtc = now, DecisionReason = reason, RowVersion = RowVersion + 1 };

    /// <summary>Transitions to Invalidated (e.g., run cancelled).</summary>
    public ApprovalRequest Invalidate(DateTimeOffset now, string reason)
        => this with { Status = ApprovalStatus.Invalidated, DecidedAtUtc = now, DecisionReason = reason, RowVersion = RowVersion + 1 };
}
