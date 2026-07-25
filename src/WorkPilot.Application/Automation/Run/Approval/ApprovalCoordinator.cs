using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation;
using WorkPilot.Domain.Automation.Run;
using WorkPilot.Domain.Automation.Run.Approval;

namespace WorkPilot.Application.Automation.Run.Approval;

/// <summary>
/// Orchestrates the PER-005 High one-time approval flow (doc 04 §11, doc 07 §9):
/// <list type="bullet">
///   <item>Create a frozen, time-boxed <see cref="ApprovalRequest"/> (10-minute decision window) and move the run to WaitingApproval.</item>
///   <item>Approve with a race guard (no double issuance) and a full precondition re-check (pending, not expired, source enabled, schema/epoch unchanged, run still waiting).</item>
///   <item>Issue a single-use <see cref="ConsentReceipt"/> (5-minute execution window) and resume the run to Queued.</item>
///   <item>Consume the receipt exactly once on execution success/failure; expiry/invalidation block reuse.</item>
/// </list>
/// The coordinator mutates the in-memory domain objects and persists via <see cref="IApprovalStore"/>;
/// the caller is responsible for persisting the returned <see cref="AutomationRun"/> and events.
/// </summary>
public sealed class ApprovalCoordinator
{
    private readonly IApprovalStore _store;
    private readonly IClock _clock;
    private readonly IIdGenerator _ids;

    public ApprovalCoordinator(IApprovalStore store, IClock clock, IIdGenerator ids)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
    }

    /// <summary>Creates a pending approval and moves the run into WaitingApproval.</summary>
    public async Task<Result<ApprovalCreated>> CreateRequestAsync(
        AutomationRun run, StepRun step, ApprovalRequestData data, CancellationToken ct)
    {
        if (run is null) return Result<ApprovalCreated>.Fail(RunErrors.NotFoundError());
        if (step is null) return Result<ApprovalCreated>.Fail(RunErrors.StepNodeNotFoundError("<none>"));

        var now = _clock.UtcNow;
        var approval = ApprovalRequest.Create(
            _ids.NewId(), run.Id, step.Id, data.SourceKind, data.SourceId, data.CapabilityStableId,
            data.SchemaSha256, data.ArgumentDigest, data.ScopeDigest, data.SafeSummaryJson,
            data.RiskLevel, data.PolicyTraceSha256, data.Epoch, now);

        var waiting = run.MarkWaitingApproval(now);
        var created = await _store.CreateRequestAsync(approval, ct);
        if (!created.IsSuccess) return Result<ApprovalCreated>.Fail(created.Error!);

        var evt = MakeEvent(run.Id, step.Id, RunEventKinds.Approval, RunEventLevel.Info,
            RunEventCodes.ApprovalCreated, new Dictionary<string, string> { ["approval_id"] = approval.Id });
        return Result<ApprovalCreated>.Ok(new ApprovalCreated(approval, waiting, new[] { evt }));
    }

    /// <summary>
    /// Approves a pending request. The race guard rejects any request that is no longer Pending
    /// (already decided by a concurrent call), and the precondition re-check blocks issuance when the
    /// source/schema/epoch/run state has changed since the request was frozen.
    /// </summary>
    public async Task<Result<ApprovalDecisionOutcome>> ApproveAsync(
        string approvalId, ApprovalDecisionContext ctx, CancellationToken ct)
    {
        var get = await _store.GetRequestAsync(approvalId, ct);
        if (!get.IsSuccess) return Result<ApprovalDecisionOutcome>.Fail(get.Error!);
        if (get.Value is not { } approval)
            return Result<ApprovalDecisionOutcome>.Fail(RunErrors.ApprovalNotFoundError(approvalId));

        // Race guard: a second approval sees a non-Pending request and fails without issuing a receipt.
        if (approval.Status != ApprovalStatus.Pending)
            return Result<ApprovalDecisionOutcome>.Fail(
                RunErrors.ApprovalAlreadyDecidedError(approvalId, approval.Status.ToString()));

        var now = _clock.UtcNow;
        var pre = approval.PrecheckApprovable(now, ctx.CurrentEpoch, ctx.SchemaShaCurrent, ctx.SourceEnabled);
        if (!pre.IsSuccess) return Result<ApprovalDecisionOutcome>.Fail(pre.Error!);
        if (!ctx.RunStillWaiting)
            return Result<ApprovalDecisionOutcome>.Fail(
                RunErrors.ApprovalPreconditionChangedError(approvalId, "run_not_waiting"));

        // Issue the one-time receipt and transition the request to Approved.
        var receipt = ConsentReceipt.Issue(_ids.NewId(), approval, 1, now);
        var approved = approval.MarkApproved(now);
        var resumed = ctx.Run.ResumeFromWait(); // WaitingApproval -> Queued

        var saveReq = await _store.SaveRequestAsync(approved, ct);
        if (!saveReq.IsSuccess) return Result<ApprovalDecisionOutcome>.Fail(saveReq.Error!);
        var saveRct = await _store.SaveReceiptAsync(receipt, ct);
        if (!saveRct.IsSuccess) return Result<ApprovalDecisionOutcome>.Fail(saveRct.Error!);

        var events = new[]
        {
            MakeEvent(approved.RunId, approved.StepId, RunEventKinds.Approval, RunEventLevel.Info,
                RunEventCodes.ApprovalApproved, new Dictionary<string, string> { ["approval_id"] = approved.Id, ["receipt_id"] = receipt.Id }),
            MakeEvent(receipt.RunId, receipt.StepId, RunEventKinds.Receipt, RunEventLevel.Info,
                RunEventCodes.ReceiptIssued, new Dictionary<string, string> { ["receipt_id"] = receipt.Id })
        };
        return Result<ApprovalDecisionOutcome>.Ok(new ApprovalDecisionOutcome(approved, receipt, resumed, events));
    }

    /// <summary>Consumes a receipt exactly once. Fails if already consumed, invalidated, or expired.</summary>
    public async Task<Result<ConsentReceipt>> ConsumeReceiptAsync(string receiptId, CancellationToken ct)
    {
        var get = await _store.GetReceiptAsync(receiptId, ct);
        if (!get.IsSuccess) return Result<ConsentReceipt>.Fail(get.Error!);
        if (get.Value is not { } receipt)
            return Result<ConsentReceipt>.Fail(RunErrors.ReceiptNotFoundError(receiptId));

        var now = _clock.UtcNow;
        var consumed = receipt.Consume(now);
        if (!consumed.IsSuccess) return Result<ConsentReceipt>.Fail(consumed.Error!);

        var finalReceipt = consumed.Value!;
        var save = await _store.SaveReceiptAsync(finalReceipt, ct);
        if (!save.IsSuccess) return Result<ConsentReceipt>.Fail(save.Error!);
        return Result<ConsentReceipt>.Ok(finalReceipt);
    }

    private RunEvent MakeEvent(RunId runId, StepRunId stepId, string kind, RunEventLevel level, string code, Dictionary<string, string> props)
    {
        var json = JsonSerializer.Serialize(props);
        return RunEvent.Create(RunEventId.Create(_ids), runId, kind, level, code, code, json,
            runId.Value, _clock.UtcNow, stepId, null);
    }
}
