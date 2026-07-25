using System;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation;
using WorkPilot.Domain.Automation.Run;
using Xunit;

namespace WorkPilot.Domain.Tests.Automation.Run;

public class RunDomainTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    private static readonly AutomationRevisionId Rev = AutomationRevisionId.Parse("rev_1");
    private static readonly RunSnapshotId Snap = RunSnapshotId.Parse("snap_1");

    private static RunSnapshot ValidSnapshot(RunSnapshotId id = default) =>
        RunSnapshot.Create(id == default ? Snap : id,
            Rev, ExpertRevisionId.Parse("exp_rev_1"),
            "{}","{}","{}","{}","{}", 0, "{\"v\":1}",
            new string('a', 64), Now);

    [Fact]
    public void Create_queued_run_has_expected_defaults()
    {
        var run = AutomationRun.Create(RunId.Parse("run_1"), Rev, Snap, RunTriggerKind.Interval, Now, Now);

        Assert.Equal(RunStatus.Queued, run.Status);
        Assert.Equal(1, run.RowVersion);
        Assert.Equal(0, run.LastEventSequence);
        Assert.Null(run.AutomationId);
        Assert.Null(run.StartedAtUtc);
        Assert.False(run.IsTerminal);
    }

    [Fact]
    public void Create_rejects_invalid_priority()
    {
        Assert.Throws<DomainException>(() =>
            AutomationRun.Create(RunId.Parse("run_1"), Rev, Snap, RunTriggerKind.Interval, Now, Now, priority: 11));
        Assert.Throws<DomainException>(() =>
            AutomationRun.Create(RunId.Parse("run_1"), Rev, Snap, RunTriggerKind.Interval, Now, Now, priority: -11));
    }

    [Fact]
    public void MarkClaimed_sets_owner_and_lease()
    {
        var run = AutomationRun.Create(RunId.Parse("run_1"), Rev, Snap, RunTriggerKind.Interval, Now, Now);
        var claimed = run.MarkClaimed("worker_7", Now.AddMinutes(1), Now);

        Assert.Equal(RunStatus.Claimed, claimed.Status);
        Assert.Equal("worker_7", claimed.LeaseOwner);
        Assert.Equal(Now.AddMinutes(1), claimed.LeaseExpiresAtUtc);
        Assert.Equal(Now, claimed.ClaimedAtUtc);
        Assert.Equal(2, claimed.RowVersion);
    }

    [Fact]
    public void MarkClaimed_throws_when_not_queued()
    {
        var run = AutomationRun.Create(RunId.Parse("run_1"), Rev, Snap, RunTriggerKind.Interval, Now, Now).MarkRunning(Now);
        Assert.Throws<DomainException>(() => run.MarkClaimed("w", Now.AddMinutes(1), Now));
    }

    [Fact]
    public void MarkRunning_sets_started_and_is_not_terminal()
    {
        var run = AutomationRun.Create(RunId.Parse("run_1"), Rev, Snap, RunTriggerKind.Interval, Now, Now).MarkClaimed("w", Now.AddMinutes(1), Now);
        var running = run.MarkRunning(Now.AddSeconds(1));
        Assert.Equal(RunStatus.Running, running.Status);
        Assert.Equal(Now.AddSeconds(1), running.StartedAtUtc);
        Assert.False(running.IsTerminal);
    }

    [Fact]
    public void MarkCompleted_is_terminal()
    {
        var run = AutomationRun.Create(RunId.Parse("run_1"), Rev, Snap, RunTriggerKind.Interval, Now, Now)
            .MarkClaimed("w", Now.AddMinutes(1), Now)
            .MarkRunning(Now.AddSeconds(1))
            .MarkCompleted(Now.AddMinutes(2));
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.True(run.IsTerminal);
        Assert.Equal(Now.AddMinutes(2), run.FinishedAtUtc);
    }

    [Fact]
    public void MarkFailed_records_error_code()
    {
        var run = AutomationRun.Create(RunId.Parse("run_1"), Rev, Snap, RunTriggerKind.Interval, Now, Now)
            .MarkFailed(Now.AddMinutes(2), "STEP_TIMEOUT");
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Equal("STEP_TIMEOUT", run.FinalErrorCode);
        Assert.True(run.IsTerminal);
    }

    [Fact]
    public void RequestCancellation_then_ApplyCancellation()
    {
        var requested = AutomationRun.Create(RunId.Parse("run_1"), Rev, Snap, RunTriggerKind.Interval, Now, Now)
            .RequestCancellation(Now.AddSeconds(5));
        Assert.Equal(Now.AddSeconds(5), requested.CancellationRequestedAtUtc);
        Assert.Equal(RunStatus.Queued, requested.Status); // not yet cancelled

        var cancelled = requested.ApplyCancellation(Now.AddSeconds(9));
        Assert.Equal(RunStatus.Cancelled, cancelled.Status);
        Assert.True(cancelled.IsTerminal);
        Assert.Equal(Now.AddSeconds(5), cancelled.CancellationRequestedAtUtc);
    }

    [Fact]
    public void Snapshot_rejects_short_canonical()
    {
        Assert.Throws<DomainException>(() =>
            RunSnapshot.Create(Snap, Rev, ExpertRevisionId.Parse("exp_rev_1"),
                "{}","{}","{}","{}","{}", 0, "{\"v\":1}", new string('a', 63), Now));
    }

    [Fact]
    public void Snapshot_rejects_empty_json()
    {
        Assert.Throws<DomainException>(() =>
            RunSnapshot.Create(Snap, Rev, ExpertRevisionId.Parse("exp_rev_1"),
                "", "{}", "{}", "{}", "{}", 0, "{\"v\":1}", new string('a', 64), Now));
    }

    [Fact]
    public void Event_WithSequence_stamps_sequence()
    {
        var ev = RunEvent.Create(RunEventId.Parse("e_1"), RunId.Parse("run_1"), "run_created",
            RunEventLevel.Info, "RUN_CREATED", "Run.Created", "{}", "corr_1", Now);
        Assert.Equal(0, ev.Sequence);
        var stamped = ev.WithSequence(7);
        Assert.Equal(7, stamped.Sequence);
    }

    [Fact]
    public void Event_rejects_empty_kind()
    {
        Assert.Throws<DomainException>(() =>
            RunEvent.Create(RunEventId.Parse("e_1"), RunId.Parse("run_1"), "",
                RunEventLevel.Info, "RUN_CREATED", "Run.Created", "{}", "corr_1", Now));
    }

    [Fact]
    public void Occurrence_rejects_bad_dedupe_key()
    {
        Assert.Throws<DomainException>(() =>
            TriggerOccurrence.Create(TriggerOccurrenceId.Parse("occ_1"), AutomationId.Parse("auto_1"), Rev,
                "interval_1", Now, Now, OccurrenceDisposition.Queued, new string('b', 63), 0, "{}"));
    }
}
