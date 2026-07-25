using System.Threading.Tasks;
using WorkPilot.App.Core.Runs;
using WorkPilot.App.Core.Tests.Fakes;
using WorkPilot.Application.Automation.Run;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation.Run;
using Xunit;

namespace WorkPilot.App.Core.Tests.Runs;

public class RerunOrchestratorTests
{
    [Fact]
    public async Task Rerun_creates_new_run_referencing_parent_with_immutable_snapshot()
    {
        var ids = new SeqIdGenerator();
        var repo = new StubRunRepository();
        var parent = RunTestFactory.MakeRun(ids, RunStatus.Completed, RunTestFactory.T0);
        var snap = RunTestFactory.MakeSnapshot(ids, "{\"target\":\"https://orig\"}");
        repo.Seed(RunTestFactory.MakeDetails(parent, snap, System.Array.Empty<StepRun>(), System.Array.Empty<RunEvent>()));

        var orch = new RerunOrchestrator(repo, ids, new StubClock());
        var result = await orch.RerunAsync(parent.Id);

        Assert.True(result.IsSuccess);
        var newId = result.Value!;
        Assert.NotEqual(parent.Id, newId);

        var getNew = await repo.GetRunAsync(newId, default);
        Assert.True(getNew.IsSuccess);
        Assert.NotNull(getNew.Value);
        Assert.Equal(parent.Id, getNew.Value!.Run.ParentRunId);              // original referenced
        Assert.Equal(parent.AutomationId, getNew.Value.Run.AutomationId);
        Assert.Equal(parent.TriggerKind, getNew.Value.Run.TriggerKind);
        Assert.Equal(parent.Priority, getNew.Value.Run.Priority);
        // Frozen snapshot cloned with new id but identical content (canonical hash preserved).
        Assert.Equal(snap.CanonicalSha256, getNew.Value.Snapshot.CanonicalSha256);
        Assert.NotEqual(snap.Id, getNew.Value.Snapshot.Id);

        // Original run history stays immutable.
        var getParent = await repo.GetRunAsync(parent.Id, default);
        Assert.Equal(parent.Id, getParent.Value!.Run.Id);
        Assert.Equal(RunStatus.Completed, getParent.Value.Run.Status);
    }

    [Fact]
    public async Task Rerun_of_missing_run_fails()
    {
        var ids = new SeqIdGenerator();
        var repo = new StubRunRepository();
        var orch = new RerunOrchestrator(repo, ids, new StubClock());

        var result = await orch.RerunAsync(RunId.Parse("missing"));

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
    }
}
