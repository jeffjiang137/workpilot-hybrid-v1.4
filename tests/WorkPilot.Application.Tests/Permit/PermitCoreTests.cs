using System;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Application.Automation.Run.Permit;
using Xunit;

namespace WorkPilot.Application.Tests.Permit;

/// <summary>Native single-use Permit registry semantics (ADR-1508, doc 07 §10-11).</summary>
public class PermitCoreTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    private static PermitLiveState GoodLive(string owner = "worker_a") => new(owner, DateTimeOffset.MaxValue, false);

    private static PermitBinding MakeBinding(ManagedPermitCore core, long epoch = 0) => new(
        WorkerProcessNonce: "proc", InvocationId: "inv1", RunId: "run_1", StepId: "step_1", Attempt: 1,
        CapabilitySourceKind: "connector", CapabilitySourceId: "acct_1", CapabilityStableId: "send_email",
        SchemaSha256: "sha", ArgumentDigest: "dig", RevocationEpoch: epoch, WorkerLeaseOwner: "worker_a",
        LeaseExpiresAtUtc: DateTimeOffset.MaxValue, ExpiresAtUtc: DateTimeOffset.UtcNow.AddSeconds(30));

    [Fact]
    public async Task Issue_then_consume_succeeds_exactly_once()
    {
        var core = new ManagedPermitCore();
        var permit = core.Issue(MakeBinding(core));

        var first = await permit.ConsumeAndCheckAsync(GoodLive());
        Assert.True(first.IsSuccess);

        var second = await permit.ConsumeAndCheckAsync(GoodLive());
        Assert.False(second.IsSuccess); // single-use
        Assert.Equal("RUN_PERMIT_CONSUMED", second.Error!.Code);
        Assert.True(permit.IsConsumed);
    }

    [Fact]
    public async Task Consume_fails_when_revocation_epoch_changed()
    {
        var core = new ManagedPermitCore();
        core.CurrentRevocationEpoch = 5; // source/grant revoked after issue
        var permit = core.Issue(MakeBinding(core, epoch: 0));

        var r = await permit.ConsumeAndCheckAsync(GoodLive());
        Assert.False(r.IsSuccess);
        Assert.Equal("RUN_PERMIT_EPOCH", r.Error!.Code);
        Assert.False(permit.IsConsumed); // never consumed -> safe to re-issue
    }

    [Fact]
    public async Task Consume_fails_on_worker_lease_mismatch()
    {
        var core = new ManagedPermitCore();
        var permit = core.Issue(MakeBinding(core)); // lease owner = worker_a

        var r = await permit.ConsumeAndCheckAsync(new PermitLiveState("other_worker", DateTimeOffset.MaxValue, false));
        Assert.False(r.IsSuccess);
        Assert.Equal("RUN_PERMIT_LEASE", r.Error!.Code);
    }

    [Fact]
    public async Task Consume_fails_when_cancellation_requested()
    {
        var core = new ManagedPermitCore();
        var permit = core.Issue(MakeBinding(core));

        var r = await permit.ConsumeAndCheckAsync(new PermitLiveState("worker_a", DateTimeOffset.MaxValue, true));
        Assert.False(r.IsSuccess);
        Assert.Equal("RUN_PERMIT_CANCELLED", r.Error!.Code);
    }

    [Fact]
    public async Task Disposing_unconsumed_lease_revokes_permit_blocking_io()
    {
        var core = new ManagedPermitCore();
        var issuer = new PermitIssuer(core, new FakeClock(Now), new SequentialIdGenerator());
        var lease = (await issuer.AcquirePermitAsync(new ApprovedInvocation(
            RunId: "run_1", StepId: "step_1", Attempt: 1,
            CapabilitySourceKind: "connector", CapabilitySourceId: "acct_1", CapabilityStableId: "send_email",
            SchemaSha256: "sha", ArgumentDigest: "dig", RevocationEpoch: 0,
            WorkerLeaseOwner: "worker_a", LeaseExpiresAtUtc: DateTimeOffset.MaxValue), CancellationToken.None)).Value!;

        lease.Dispose(); // abandoned before send -> revoked

        var after = await lease.ConsumeAndCheckAsync(CancellationToken.None);
        Assert.False(after.IsSuccess); // cannot be replayed
    }
}
