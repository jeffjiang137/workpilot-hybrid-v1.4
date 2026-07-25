using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Domain.Automation.Run;
using WorkPilot.Domain.Automation.Run.Approval;

namespace WorkPilot.Application.Automation.Run.Approval;

/// <summary>
/// Persistence port for approval requests and their one-time consent receipts (PER-005, doc 04 §11).
/// Implemented by the Infrastructure layer against the <c>approval_requests</c> table (Migration 018)
/// plus an in-memory receipt store. All operations return <see cref="Result"/>.
/// </summary>
public interface IApprovalStore
{
    Task<Result> CreateRequestAsync(ApprovalRequest request, CancellationToken ct);
    Task<Result<ApprovalRequest?>> GetRequestAsync(string id, CancellationToken ct);
    Task<Result> SaveRequestAsync(ApprovalRequest request, CancellationToken ct);
    Task<Result<ConsentReceipt?>> GetReceiptAsync(string receiptId, CancellationToken ct);
    Task<Result<ConsentReceipt?>> GetReceiptByApprovalAsync(string approvalId, CancellationToken ct);
    Task<Result> SaveReceiptAsync(ConsentReceipt receipt, CancellationToken ct);
}

/// <summary>Frozen context needed to create a High one-time approval (PER-005).</summary>
public sealed record ApprovalRequestData(
    string SourceKind,
    string SourceId,
    string CapabilityStableId,
    string SchemaSha256,
    string ArgumentDigest,
    string ScopeDigest,
    string SafeSummaryJson,
    int RiskLevel,
    string PolicyTraceSha256,
    long Epoch);

/// <summary>Outcome of creating an approval request: the pending request + run moved to WaitingApproval.</summary>
public sealed record ApprovalCreated(
    ApprovalRequest Approval,
    AutomationRun RunWaitingApproval,
    IReadOnlyList<RunEvent> Events);

/// <summary>Re-evaluation context for approval (doc 04 §11 / PER-005).</summary>
public sealed record ApprovalDecisionContext(
    AutomationRun Run,
    long CurrentEpoch,
    string SchemaShaCurrent,
    bool SourceEnabled,
    bool RunStillWaiting);

/// <summary>Outcome of approving: the approved request, the one-time receipt, and the resumed run.</summary>
public sealed record ApprovalDecisionOutcome(
    ApprovalRequest Approved,
    ConsentReceipt Receipt,
    AutomationRun ResumedRun,
    IReadOnlyList<RunEvent> Events);
