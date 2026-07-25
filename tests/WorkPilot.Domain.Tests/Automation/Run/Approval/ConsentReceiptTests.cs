using System;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation.Run;
using WorkPilot.Domain.Automation.Run.Approval;
using Xunit;

namespace WorkPilot.Domain.Tests.Automation.Run.Approval;

public class ConsentReceiptTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    private static readonly SequentialIdGenerator Ids = new();
    private static readonly RunId Run = RunId.Parse("run_1");
    private static readonly StepRunId Step = StepRunId.Create(Ids);

    private static ApprovalRequest Pending()
        => ApprovalRequest.Create("apr_1", Run, Step, "connector", "acct_1", "send_email",
            "schema_sha", "arg_sha", "scope_sha", "{}", 2, "trace_sha", epoch: 7, Now);

    private static ConsentReceipt Issued()
        => ConsentReceipt.Issue("rcpt_1", Pending(), attempt: 1, Now);

    [Fact]
    public void Issue_is_one_time_with_five_minute_window()
    {
        var r = Issued();
        Assert.Equal(ReceiptStatus.Issued, r.Status);
        Assert.Equal(Now.AddMinutes(Limits.V1_5.ConsentReceiptExecutionWindowMinutes), r.ExpiresAtUtc);
        Assert.Equal("schema_sha", r.SchemaSha256);
        Assert.Equal(7, r.Epoch);
    }

    [Fact]
    public void Consume_succeeds_once()
    {
        var consumed = Issued().Consume(Now.AddMinutes(1));
        Assert.True(consumed.IsSuccess);
        Assert.Equal(ReceiptStatus.Consumed, consumed.Value!.Status);
        Assert.Equal(Now.AddMinutes(1), consumed.Value.ConsumedAtUtc);
    }

    [Fact]
    public void Consume_is_single_use_second_consume_fails()
    {
        var first = Issued().Consume(Now.AddMinutes(1));
        var second = first.Value!.Consume(Now.AddMinutes(2));
        Assert.True(second.IsSuccess == false);
        Assert.Equal("RUN_RECEIPT_CONSUMED", second.Error!.Code);
    }

    [Fact]
    public void Consume_after_expiry_fails()
    {
        var r = Issued().Consume(Now.AddMinutes(6));
        Assert.True(r.IsSuccess == false);
        Assert.Equal("RUN_RECEIPT_EXPIRED", r.Error!.Code);
    }

    [Fact]
    public void IsExpired_true_only_after_window()
    {
        var r = Issued();
        Assert.False(r.IsExpired(Now.AddMinutes(4)));
        Assert.True(r.IsExpired(Now.AddMinutes(6)));
    }
}
