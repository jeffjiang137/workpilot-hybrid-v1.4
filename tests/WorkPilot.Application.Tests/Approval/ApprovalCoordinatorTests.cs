using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation;
using WorkPilot.Domain.Automation.Run;
using WorkPilot.Domain.Automation.Run.Approval;
using WorkPilot.Application.Automation.Run.Approval;
using Xunit;

namespace WorkPilot.Application.Tests.Approval;

public class ApprovalCoordinatorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    private static readonly SequentialIdGenerator Ids = new();
    private static readonly RunId RunId = RunId.Parse("run_1");
    private static readonly StepRunId StepId = StepRunId.Create(Ids);

    private static AutomationRun RunningRun()
        => AutomationRun.Create(RunId, AutomationRevisionId.Parse("rev_1"), RunSnapshotId.Parse("snap_1"),
            RunTriggerKind.Interval, Now, Now).MarkClaimed("w", Now.AddMinutes(1), Now).MarkRunning(Now);

    private static StepRun DummyStep()
        => StepRun.Create(StepId, RunId, "cap_1", "capability_call", "idem_1", "digest_1", attempt: 1);

    private static ApprovalRequestData Data()
        => new("connector", "acct_1", "send_email", "schema_sha", "arg_sha", "scope_sha", "{}", 2, "trace_sha", Epoch: 7);

    [Fact]
    public async Task Create_moves_run_to_waiting_approval()
    {
        var store = new FakeApprovalStore();
        var coord = new ApprovalCoordinator(store, new FakeClock(Now), Ids);

        var created = await coord.CreateRequestAsync(RunningRun(), DummyStep(), Data(), CancellationToken.None);

        Assert.True(created.IsSuccess);
        Assert.Equal(ApprovalStatus.Pending, created.Value!.Approval.Status);
        Assert.Equal(RunStatus.WaitingApproval, created.Value.RunWaitingApproval.Status);
        Assert.Single(created.Value.Events);
    }

    [Fact]
    public async Task Approve_issues_receipt_and_resumes_run()
    {
        var store = new FakeApprovalStore();
        var coord = new ApprovalCoordinator(store, new FakeClock(Now), Ids);
        var created = (await coord.CreateRequestAsync(RunningRun(), DummyStep(), Data(), CancellationToken.None)).Value!;

        var ctx = new ApprovalDecisionContext(created.RunWaitingApproval, CurrentEpoch: 7,
            SchemaShaCurrent: "schema_sha", SourceEnabled: true, RunStillWaiting: true);
        var outcome = await coord.ApproveAsync(created.Approval.Id, ctx, CancellationToken.None);

        Assert.True(outcome.IsSuccess);
        Assert.Equal(ReceiptStatus.Issued, outcome.Value!.Receipt.Status);
        Assert.Equal(ApprovalStatus.Approved, outcome.Value.Approved.Status);
        Assert.Equal(RunStatus.Queued, outcome.Value.ResumedRun.Status);
    }

    [Fact]
    public async Task Approve_is_race_safe_no_double_issuance()
    {
        var store = new FakeApprovalStore();
        var coord = new ApprovalCoordinator(store, new FakeClock(Now), Ids);
        var created = (await coord.CreateRequestAsync(RunningRun(), DummyStep(), Data(), CancellationToken.None)).Value!;
        var ctx = new ApprovalDecisionContext(created.RunWaitingApproval, 7, "schema_sha", true, true);

        var first = await coord.ApproveAsync(created.Approval.Id, ctx, CancellationToken.None);
        var second = await coord.ApproveAsync(created.Approval.Id, ctx, CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess == false);
        Assert.Equal("RUN_APPROVAL_ALREADY_DECIDED", second.Error!.Code);
    }

    [Fact]
    public async Task Approve_fails_when_expired()
    {
        var store = new FakeApprovalStore();
        // Create with a clock at Now (expires at Now+10m), then approve with a clock past the window.
        var created = (await new ApprovalCoordinator(store, new FakeClock(Now), Ids)
            .CreateRequestAsync(RunningRun(), DummyStep(), Data(), CancellationToken.None)).Value!;
        var approveCoord = new ApprovalCoordinator(store, new FakeClock(Now.AddMinutes(11)), Ids);
        var ctx = new ApprovalDecisionContext(created.RunWaitingApproval, 7, "schema_sha", true, true);

        var outcome = await approveCoord.ApproveAsync(created.Approval.Id, ctx, CancellationToken.None);
        Assert.True(outcome.IsSuccess == false);
        Assert.Equal("RUN_APPROVAL_EXPIRED", outcome.Error!.Code);
    }

    [Fact]
    public async Task Approve_fails_when_epoch_or_schema_changed()
    {
        var store = new FakeApprovalStore();
        var coord = new ApprovalCoordinator(store, new FakeClock(Now), Ids);
        var created = (await coord.CreateRequestAsync(RunningRun(), DummyStep(), Data(), CancellationToken.None)).Value!;

        var epochChanged = new ApprovalDecisionContext(created.RunWaitingApproval, CurrentEpoch: 8, "schema_sha", true, true);
        Assert.Equal("RUN_APPROVAL_PRECONDITION_CHANGED",
            (await coord.ApproveAsync(created.Approval.Id, epochChanged, CancellationToken.None)).Error!.Code);

        var schemaChanged = new ApprovalDecisionContext(created.RunWaitingApproval, 7, "other_sha", true, true);
        Assert.Equal("RUN_APPROVAL_PRECONDITION_CHANGED",
            (await coord.ApproveAsync(created.Approval.Id, schemaChanged, CancellationToken.None)).Error!.Code);
    }

    [Fact]
    public async Task Consume_receipt_is_single_use()
    {
        var store = new FakeApprovalStore();
        var coord = new ApprovalCoordinator(store, new FakeClock(Now), Ids);
        var created = (await coord.CreateRequestAsync(RunningRun(), DummyStep(), Data(), CancellationToken.None)).Value!;
        var ctx = new ApprovalDecisionContext(created.RunWaitingApproval, 7, "schema_sha", true, true);
        var receipt = (await coord.ApproveAsync(created.Approval.Id, ctx, CancellationToken.None)).Value!.Receipt;

        var first = await coord.ConsumeReceiptAsync(receipt.Id, CancellationToken.None);
        var second = await coord.ConsumeReceiptAsync(receipt.Id, CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.Equal(ReceiptStatus.Consumed, first.Value!.Status);
        Assert.True(second.IsSuccess == false);
        Assert.Equal("RUN_RECEIPT_CONSUMED", second.Error!.Code);
    }

    private sealed class FakeApprovalStore : IApprovalStore
    {
        private readonly Dictionary<string, ApprovalRequest> _reqs = new();
        private readonly Dictionary<string, ConsentReceipt> _rcpts = new();

        public Task<Result> CreateRequestAsync(ApprovalRequest request, CancellationToken ct)
        { _reqs[request.Id] = request; return Task.FromResult(Result.Success()); }

        public Task<Result<ApprovalRequest?>> GetRequestAsync(string id, CancellationToken ct)
            => Task.FromResult(Result<ApprovalRequest?>.Ok(_reqs.TryGetValue(id, out var r) ? r : null));

        public Task<Result> SaveRequestAsync(ApprovalRequest request, CancellationToken ct)
        { _reqs[request.Id] = request; return Task.FromResult(Result.Success()); }

        public Task<Result<ConsentReceipt?>> GetReceiptAsync(string receiptId, CancellationToken ct)
            => Task.FromResult(Result<ConsentReceipt?>.Ok(_rcpts.TryGetValue(receiptId, out var x) ? x : null));

        public Task<Result<ConsentReceipt?>> GetReceiptByApprovalAsync(string approvalId, CancellationToken ct)
            => Task.FromResult(Result<ConsentReceipt?>.Ok(_rcpts.Values.FirstOrDefault(x => x.ApprovalRequestId == approvalId)));

        public Task<Result> SaveReceiptAsync(ConsentReceipt receipt, CancellationToken ct)
        { _rcpts[receipt.Id] = receipt; return Task.FromResult(Result.Success()); }
    }
}
