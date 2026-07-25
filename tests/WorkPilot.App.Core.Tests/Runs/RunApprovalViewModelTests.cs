using System;
using System.Threading.Tasks;
using WorkPilot.App.Core.Runs;
using WorkPilot.App.Core.Tests.Fakes;
using WorkPilot.Application.Automation.Run;
using WorkPilot.Application.Automation.Run.Approval;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation.Run;
using WorkPilot.Domain.Automation.Run.Approval;
using Xunit;

namespace WorkPilot.App.Core.Tests.Runs;

public class RunApprovalViewModelTests
{
    private static (RunWithDetails details, ApprovalRequest approval) SeedWaiting(
        StubRunRepository repo, StubApprovalStore store, SeqIdGenerator ids, string approvalId, int priority = 2)
    {
        var run = RunTestFactory.MakeRun(ids, RunStatus.WaitingApproval, RunTestFactory.T0, priority: priority);
        var snap = RunTestFactory.MakeSnapshot(ids, "{\"target\":\"https://dest\"}");
        var step = RunTestFactory.MakeStep(ids, run.Id, "n1", "capability");
        var evt = RunTestFactory.MakeEvent(ids, run.Id, step.Id, RunEventCodes.ApprovalCreated,
            $"{{\"approval_id\":\"{approvalId}\"}}", 1, RunTestFactory.T0);
        var details = RunTestFactory.MakeDetails(run, snap, new[] { step }, new[] { evt });
        repo.Seed(details);

        var approval = ApprovalRequest.Create(approvalId, run.Id, step.Id, "capability", "src", "cap1",
            "sha256current", "argdig", "scopedig", "{\"target\":\"https://dest\"}", 3, "ptrace", 7, RunTestFactory.T0);
        store.Seed(approval);
        return (details, approval);
    }

    [Fact]
    public async Task LoadPending_surfaces_one_prompt_per_waiting_approval()
    {
        var ids = new SeqIdGenerator();
        var repo = new StubRunRepository();
        var store = new StubApprovalStore();
        SeedWaiting(repo, store, ids, "apr_1");

        var coordinator = new ApprovalCoordinator(store, new StubClock(), ids);
        var vm = new RunApprovalViewModel(repo, coordinator, new StubClock());
        await vm.LoadPendingAsync();

        Assert.Single(vm.Prompts);
        var p = vm.Prompts[0];
        Assert.Equal("apr_1", p.ApprovalId);
        Assert.Equal(2, p.RiskLevel); // derived from run priority (2) via min(3, priority)
        Assert.True(p.ExpiresAtUtc > p.CreatedAtUtc);
        Assert.False(p.IsExpired(p.CreatedAtUtc));
    }

    [Fact]
    public async Task Approve_removes_prompt_and_marks_request_approved()
    {
        var ids = new SeqIdGenerator();
        var repo = new StubRunRepository();
        var store = new StubApprovalStore();
        var (details, _) = SeedWaiting(repo, store, ids, "apr_1");

        var coordinator = new ApprovalCoordinator(store, new StubClock(), ids);
        var vm = new RunApprovalViewModel(repo, coordinator, new StubClock());
        await vm.LoadPendingAsync();
        var prompt = vm.Prompts[0];

        var ctx = new ApprovalDecisionContext(details.Run, 7, "sha256current", true, true);
        var r = await vm.ApproveAsync(prompt, ctx);

        Assert.True(r.IsSuccess);
        Assert.Empty(vm.Prompts);
        var stored = await store.GetRequestAsync("apr_1", default);
        Assert.Equal(ApprovalStatus.Approved, stored.Value!.Status);
    }

    [Fact]
    public async Task Dismiss_does_not_call_coordinator_run_stays_waiting()
    {
        var ids = new SeqIdGenerator();
        var repo = new StubRunRepository();
        var store = new StubApprovalStore();
        SeedWaiting(repo, store, ids, "apr_1");

        var coordinator = new ApprovalCoordinator(store, new StubClock(), ids);
        var vm = new RunApprovalViewModel(repo, coordinator, new StubClock());
        await vm.LoadPendingAsync();
        var prompt = vm.Prompts[0];

        vm.DismissAsync(prompt);

        Assert.Empty(vm.Prompts);
        var stored = await store.GetRequestAsync("apr_1", default);
        Assert.Equal(ApprovalStatus.Pending, stored.Value!.Status); // no decision was made
    }

    [Fact]
    public void ApprovalPrompt_expired_when_past_window()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var past = now.AddMinutes(-1);
        var prompt = new ApprovalPrompt(RunId.Parse("r"), "a", StepRunId.Parse("s"), "{}", 1, past, past);

        Assert.True(prompt.IsExpired(now));
        Assert.True(prompt.Remaining(now) < TimeSpan.Zero);
    }
}
