using System;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation.Run;
using WorkPilot.Domain.Automation.Run.Approval;
using Xunit;

namespace WorkPilot.Domain.Tests.Automation.Run.Approval;

public class ApprovalRequestTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    private static readonly SequentialIdGenerator Ids = new();
    private static readonly RunId Run = RunId.Parse("run_1");
    private static readonly StepRunId Step = StepRunId.Create(Ids);

    private static ApprovalRequest Pending()
        => ApprovalRequest.Create("apr_1", Run, Step, "connector", "acct_1", "send_email",
            "schema_sha", "arg_sha", "scope_sha", "{}", 2, "trace_sha", epoch: 7, Now);

    [Fact]
    public void Create_is_pending_with_ten_minute_window()
    {
        var a = Pending();
        Assert.Equal(ApprovalStatus.Pending, a.Status);
        Assert.Equal(Now.AddMinutes(Limits.V1_5.ApprovalDecisionWindowMinutes), a.ExpiresAtUtc);
        Assert.Equal(7, a.Epoch);
    }

    [Fact]
    public void IsExpired_true_only_after_window()
    {
        var a = Pending();
        Assert.False(a.IsExpired(Now.AddMinutes(9)));
        Assert.True(a.IsExpired(Now.AddMinutes(11)));
    }

    [Fact]
    public void Precheck_passes_when_all_unchanged()
    {
        var a = Pending();
        var r = a.PrecheckApprovable(Now.AddMinutes(5), currentEpoch: 7, schemaShaCurrent: "schema_sha", sourceEnabled: true);
        Assert.True(r.IsSuccess);
    }

    [Fact]
    public void Precheck_fails_when_expired()
    {
        var a = Pending();
        var r = a.PrecheckApprovable(Now.AddMinutes(11), currentEpoch: 7, schemaShaCurrent: "schema_sha", sourceEnabled: true);
        Assert.True(r.IsSuccess == false);
        Assert.Equal("RUN_APPROVAL_EXPIRED", r.Error!.Code);
    }

    [Fact]
    public void Precheck_fails_when_source_disabled()
    {
        var r = Pending().PrecheckApprovable(Now, currentEpoch: 7, schemaShaCurrent: "schema_sha", sourceEnabled: false);
        Assert.Equal("RUN_APPROVAL_PRECONDITION_CHANGED", r.Error!.Code);
    }

    [Fact]
    public void Precheck_fails_when_schema_changed()
    {
        var r = Pending().PrecheckApprovable(Now, currentEpoch: 7, schemaShaCurrent: "other_sha", sourceEnabled: true);
        Assert.Equal("RUN_APPROVAL_PRECONDITION_CHANGED", r.Error!.Code);
    }

    [Fact]
    public void Precheck_fails_when_epoch_changed()
    {
        var r = Pending().PrecheckApprovable(Now, currentEpoch: 8, schemaShaCurrent: "schema_sha", sourceEnabled: true);
        Assert.Equal("RUN_APPROVAL_PRECONDITION_CHANGED", r.Error!.Code);
    }

    [Fact]
    public void Expire_from_pending_succeeds_but_not_from_decided()
    {
        Assert.True(Pending().Expire(Now.AddMinutes(11)).IsSuccess);
        var decided = Pending().MarkApproved(Now);
        Assert.True(decided.Expire(Now.AddMinutes(11)).IsSuccess == false);
    }

    [Fact]
    public void MarkApproved_transitions_state()
    {
        var approved = Pending().MarkApproved(Now);
        Assert.Equal(ApprovalStatus.Approved, approved.Status);
        Assert.Equal(Now, approved.DecidedAtUtc);
    }
}
