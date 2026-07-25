using System;
using System.Linq;
using System.Threading.Tasks;
using WorkPilot.App.Core.Runs;
using WorkPilot.App.Core.Tests.Fakes;
using WorkPilot.Application.Automation.Run;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation.Run;
using Xunit;

namespace WorkPilot.App.Core.Tests.Runs;

public class RunDetailViewModelTests
{
    private static readonly DateTimeOffset T0 = RunTestFactory.T0;

    private static RunWithDetails MakeDetailed(SeqIdGenerator ids, params (string Code, string ToStatus)[] transitions)
    {
        var run = RunTestFactory.MakeRun(ids, RunStatus.Running, T0);
        var snap = RunTestFactory.MakeSnapshot(ids, "{\"webhookUrl\":\"https://example/secret\"}");
        var step = RunTestFactory.MakeStep(ids, run.Id, "n1", "agent", started: T0, output: "{\"email\":\"a@b.com\"}");
        var events = new System.Collections.Generic.List<RunEvent>
        {
            RunTestFactory.MakeEvent(ids, run.Id, step.Id, "RUN_STARTED", "{}", 1, T0)
        };
        var seq = 1;
        foreach (var t in transitions)
        {
            seq++;
            events.Add(RunTestFactory.MakeEvent(ids, run.Id, step.Id, t.Code, $"{{\"to_status\":\"{t.ToStatus}\"}}", seq, T0.AddMinutes(seq)));
        }
        seq++;
        events.Add(RunTestFactory.MakeEvent(ids, run.Id, step.Id, "RUN_COMPLETED", "{\"to_status\":\"completed\"}", seq, T0.AddMinutes(seq)));
        return RunTestFactory.MakeDetails(run, snap, new[] { step }, events);
    }

    [Fact]
    public async Task Load_projects_steps_transitions_and_event_count()
    {
        var ids = new SeqIdGenerator();
        var repo = new StubRunRepository();
        var d = MakeDetailed(ids, ("NODE_ENTERED", "running"));
        repo.Seed(d);

        var vm = new RunDetailViewModel(repo);
        await vm.LoadAsync(d.Run.Id);

        Assert.NotNull(vm.Detail);
        Assert.Single(vm.Detail!.Steps);
        Assert.Equal(3, vm.Detail.EventCount); // start, node_entered, completed
        Assert.Contains(vm.Detail.Transitions, tr => tr.To == RunStatus.Completed);
        Assert.Contains(vm.Detail.Transitions, tr => tr.To == RunStatus.Running);
    }

    [Fact]
    public async Task SafeSummary_redacts_target_fields_and_keeps_sizes()
    {
        var ids = new SeqIdGenerator();
        var repo = new StubRunRepository();
        var d = MakeDetailed(ids);
        repo.Seed(d);

        var vm = new RunDetailViewModel(repo);
        await vm.LoadAsync(d.Run.Id);

        Assert.True(vm.SafeSummary.HasTarget);
        var input = vm.SafeSummary.Inputs.Single(f => f.Name == "webhookUrl");
        Assert.True(input.IsTarget);
        Assert.NotNull(input.TargetAlias);
        Assert.Equal(16, input.TargetAlias!.Length);          // truncated SHA-256 alias
        Assert.True(input.ByteSize > 0);
        Assert.DoesNotContain("example/secret", input.TargetAlias); // original value never retained
        var output = vm.SafeSummary.Outputs.Single(f => f.Name == "email");
        Assert.True(output.IsTarget);
    }

    [Fact]
    public async Task Get_failure_sets_error_and_no_detail()
    {
        var ids = new SeqIdGenerator();
        var repo = new StubRunRepository();
        repo.GetError = new WorkPilot.Contracts.Primitives.AppError("E_GET", WorkPilot.Contracts.Primitives.ErrorCategory.Database, "Run.GetFailed", false);

        var vm = new RunDetailViewModel(repo);
        await vm.LoadAsync(RunId.Parse("missing"));

        Assert.True(vm.HasError);
        Assert.Null(vm.Detail);
    }

    [Fact]
    public async Task Live_events_merge_until_gap_then_refill_closes_gap()
    {
        var ids = new SeqIdGenerator();
        var repo = new StubRunRepository();
        var run = RunTestFactory.MakeRun(ids, RunStatus.Running, T0);
        var evs = new System.Collections.Generic.List<RunEvent>
        {
            RunTestFactory.MakeEvent(ids, run.Id, null, "E1", "{}", 1, T0),
            RunTestFactory.MakeEvent(ids, run.Id, null, "E2", "{}", 2, T0.AddMinutes(1)),
            RunTestFactory.MakeEvent(ids, run.Id, null, "E3", "{}", 3, T0.AddMinutes(2))
        };
        repo.Seed(RunTestFactory.MakeDetails(run, RunTestFactory.MakeSnapshot(ids), Array.Empty<StepRun>(), evs));

        var vm = new RunDetailViewModel(repo);
        await vm.LoadAsync(run.Id);
        Assert.Equal(3, vm.Detail!.EventCount);

        var feed = new InMemoryRunFeed();
        vm.AttachFeed(feed, run.Id);
        feed.Publish(new RunFeedItem(run.Id, new[]
        {
            RunTestFactory.MakeEvent(ids, run.Id, null, "E4", "{}", 4, T0.AddMinutes(3)),
            RunTestFactory.MakeEvent(ids, run.Id, null, "E5", "{}", 5, T0.AddMinutes(4))
        }, false));
        Assert.Equal(5, vm.Detail.EventCount);
        Assert.False(vm.IsLiveGap);

        // A gap (sequence 8, skipping 6/7) must flag IsLiveGap without merging.
        feed.Publish(new RunFeedItem(run.Id, new[]
        {
            RunTestFactory.MakeEvent(ids, run.Id, null, "E8", "{}", 8, T0.AddMinutes(8))
        }, false));
        Assert.True(vm.IsLiveGap);
        Assert.Equal(5, vm.Detail.EventCount);

        // Simulate the host having persisted the live events; a refill returns the complete set.
        var e4 = RunTestFactory.MakeEvent(ids, run.Id, null, "E4", "{}", 4, T0.AddMinutes(3));
        var e5 = RunTestFactory.MakeEvent(ids, run.Id, null, "E5", "{}", 5, T0.AddMinutes(4));
        repo.Seed(RunTestFactory.MakeDetails(run, RunTestFactory.MakeSnapshot(ids), Array.Empty<StepRun>(),
            new[] { evs[0], evs[1], evs[2], e4, e5 }));

        await vm.RefillGapAsync();
        Assert.False(vm.IsLiveGap);
        Assert.Equal(5, vm.Detail.EventCount);
    }

    [Fact]
    public async Task Cancel_requests_cancellation_and_reloads()
    {
        var ids = new SeqIdGenerator();
        var repo = new StubRunRepository();
        var run = RunTestFactory.MakeRun(ids, RunStatus.Running, T0);
        repo.Seed(RunTestFactory.MakeDetails(run, RunTestFactory.MakeSnapshot(ids), Array.Empty<StepRun>(), Array.Empty<RunEvent>()));

        var vm = new RunDetailViewModel(repo);
        await vm.LoadAsync(run.Id);
        Assert.Null(vm.Detail!.FinalErrorCode);

        // Execute is async void; poll until the cancellation is persisted and reloaded.
        vm.CancelCommand.Execute(null);
        bool requested = false;
        for (var i = 0; i < 100; i++)
        {
            await Task.Delay(5);
            var reloaded = await repo.GetRunAsync(run.Id, default);
            if (reloaded.Value!.Run.CancellationRequestedAtUtc is not null) { requested = true; break; }
        }
        Assert.True(requested);
    }
}
