using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Application.Automation.Run.Approval;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Domain.Automation.Run.Approval;

namespace WorkPilot.App.Core.Tests.Fakes;

/// <summary>In-memory <see cref="IApprovalStore"/> for App.Core approval view-model tests.</summary>
public sealed class StubApprovalStore : IApprovalStore
{
    private readonly Dictionary<string, ApprovalRequest> _requests = new();
    private readonly Dictionary<string, ConsentReceipt> _receipts = new();

    public void Seed(ApprovalRequest r) => _requests[r.Id] = r;
    public void Seed(ConsentReceipt r) => _receipts[r.Id] = r;

    public Task<Result> CreateRequestAsync(ApprovalRequest request, CancellationToken ct)
    {
        _requests[request.Id] = request;
        return Task.FromResult(Result.Success());
    }

    public Task<Result<ApprovalRequest?>> GetRequestAsync(string id, CancellationToken ct)
        => Task.FromResult(Result<ApprovalRequest?>.Ok(_requests.TryGetValue(id, out var r) ? r : null));

    public Task<Result> SaveRequestAsync(ApprovalRequest request, CancellationToken ct)
    {
        _requests[request.Id] = request;
        return Task.FromResult(Result.Success());
    }

    public Task<Result<ConsentReceipt?>> GetReceiptAsync(string receiptId, CancellationToken ct)
        => Task.FromResult(Result<ConsentReceipt?>.Ok(_receipts.TryGetValue(receiptId, out var r) ? r : null));

    public Task<Result<ConsentReceipt?>> GetReceiptByApprovalAsync(string approvalId, CancellationToken ct)
        => Task.FromResult(Result<ConsentReceipt?>.Ok(_receipts.Values.FirstOrDefault(r => r.ApprovalRequestId == approvalId)));

    public Task<Result> SaveReceiptAsync(ConsentReceipt receipt, CancellationToken ct)
    {
        _receipts[receipt.Id] = receipt;
        return Task.FromResult(Result.Success());
    }
}
