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

public class RunListViewModelTests
{
    private static RunWithDetails SeedRun(StubRunRepository repo, SeqIdGenerator ids, RunStatus status, DateTimeOffset? started, int priority = 0)
    {
        var run = RunTestFactory.MakeRun(ids, status, started, priority: priority);
        var details = RunTestFactory.MakeDetails(run, RunTestFactory.MakeSnapshot(ids), Array.Empty<StepRun>(), Array.Empty<RunEvent>());
        repo.Seed(details);
        return details;
    }

    [Fact]
    public async Task First_page_loads_all_when_under_page_size()
    {
        var ids = new SeqIdGenerator();
        var repo = new StubRunRepository();
        for (var i = 0; i < 12; i++)
            SeedRun(repo, ids, RunStatus.Completed, RunTestFactory.T0.AddMinutes(i));

        var vm = new RunListViewModel(repo);
        await vm.LoadFirstPageAsync();

        Assert.False(vm.IsLoading);
        Assert.Equal(12, vm.Items.Count);
        Assert.False(vm.HasMore);
        Assert.False(vm.HasError);
        Assert.False(vm.IsEmpty);
    }

    [Fact]
    public async Task Pagination_uses_stable_cursor_and_reports_has_more()
    {
        var ids = new SeqIdGenerator();
        var repo = new StubRunRepository();
        for (var i = 0; i < 120; i++)
            SeedRun(repo, ids, RunStatus.Completed, RunTestFactory.T0.AddMinutes(i));

        var vm = new RunListViewModel(repo);
        await vm.LoadFirstPageAsync();
        Assert.Equal(50, vm.Items.Count);
        Assert.True(vm.HasMore);

        await vm.LoadNextPageAsync();
        Assert.Equal(100, vm.Items.Count);
        await vm.LoadNextPageAsync();
        Assert.Equal(120, vm.Items.Count);
        Assert.False(vm.HasMore);
    }

    [Fact]
    public async Task Status_filter_only_returns_matching_runs()
    {
        var ids = new SeqIdGenerator();
        var repo = new StubRunRepository();
        SeedRun(repo, ids, RunStatus.Completed, RunTestFactory.T0);
        SeedRun(repo, ids, RunStatus.Failed, RunTestFactory.T0.AddMinutes(1));
        SeedRun(repo, ids, RunStatus.Running, RunTestFactory.T0.AddMinutes(2));

        var vm = new RunListViewModel(repo) { StatusFilter = RunStatus.Failed };
        // The filter setter triggers a reload; with a completed-task repository it completes synchronously.
        for (var i = 0; i < 50 && (vm.IsLoading || vm.Items.Count != 1); i++)
            await Task.Delay(5);

        Assert.Single(vm.Items);
        Assert.Equal(RunStatus.Failed, vm.Items[0].Status);
    }

    [Fact]
    public async Task From_to_window_filters_by_started_time()
    {
        var ids = new SeqIdGenerator();
        var repo = new StubRunRepository();
        SeedRun(repo, ids, RunStatus.Completed, RunTestFactory.T0.AddMinutes(0));
        SeedRun(repo, ids, RunStatus.Completed, RunTestFactory.T0.AddMinutes(10));
        SeedRun(repo, ids, RunStatus.Completed, RunTestFactory.T0.AddMinutes(20));

        var vm = new RunListViewModel(repo)
        {
            FromUtcFilter = RunTestFactory.T0.AddMinutes(5),
            ToUtcFilter = RunTestFactory.T0.AddMinutes(15)
        };
        for (var i = 0; i < 50 && (vm.IsLoading || vm.Items.Count != 1); i++)
            await Task.Delay(5);

        Assert.Single(vm.Items);
        Assert.Equal(RunTestFactory.T0.AddMinutes(10), vm.Items[0].StartedAtUtc);
    }

    [Fact]
    public async Task List_failure_sets_error_and_clears_items()
    {
        var ids = new SeqIdGenerator();
        var repo = new StubRunRepository();
        repo.ListError = new AppError("E_LIST", ErrorCategory.Internal, "Run.ListFailed", false);
        var vm = new RunListViewModel(repo);

        await vm.LoadFirstPageAsync();

        Assert.True(vm.HasError);
        Assert.Equal("E_LIST", vm.Error!.Code);
        Assert.Empty(vm.Items);
    }

    [Fact]
    public async Task Empty_result_after_filter_is_flagged_as_no_result_not_generic_empty()
    {
        var ids = new SeqIdGenerator();
        var repo = new StubRunRepository();
        SeedRun(repo, ids, RunStatus.Completed, RunTestFactory.T0);

        var vm = new RunListViewModel(repo) { StatusFilter = RunStatus.Failed };
        for (var i = 0; i < 50 && vm.IsLoading; i++)
            await Task.Delay(5);

        Assert.True(vm.IsEmpty);
        Assert.True(vm.HasNoResult);
    }
}
