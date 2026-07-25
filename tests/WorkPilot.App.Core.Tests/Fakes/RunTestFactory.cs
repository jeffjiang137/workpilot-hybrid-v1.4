using System;
using System.Collections.Generic;
using WorkPilot.Application.Automation.Run;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation.Run;

namespace WorkPilot.App.Core.Tests.Fakes;

/// <summary>Builders for run aggregates/snapshots/steps/events used across the App.Core Runs tests.</summary>
public static class RunTestFactory
{
    /// <summary>64-char hex canonical hash accepted by <see cref="RunSnapshot.Create"/>.</summary>
    public const string SampleCanonical = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    public static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static RunSnapshot MakeSnapshot(IIdGenerator ids, string? capabilityJson = null) =>
        RunSnapshot.Create(RunSnapshotId.Create(ids), AutomationRevisionId.Create(ids), ExpertRevisionId.Create(ids),
            "{\"policy\":\"p\"}", capabilityJson ?? "{\"webhookUrl\":\"https://example/secret\"}",
            "{\"wf\":1}", "{\"b\":1}", "{\"bud\":1}",
            1, "{\"algo\":1}", SampleCanonical, T0);

    public static AutomationRun MakeRun(IIdGenerator ids, RunStatus status = RunStatus.Completed,
        DateTimeOffset? started = null, RunId? parent = null, int priority = 0)
    {
        var run = AutomationRun.Create(RunId.Create(ids), AutomationRevisionId.Create(ids), RunSnapshotId.Create(ids),
            RunTriggerKind.Manual, T0, T0, AutomationId.Create(ids), null, parent, priority);

        if (status == RunStatus.Queued)
        {
            // leave as Queued (no StartedAtUtc)
        }
        else if (status == RunStatus.Claimed)
        {
            run = run.MarkClaimed("owner", T0.AddMinutes(5), T0);
        }
        else if (status == RunStatus.WaitingDelay)
        {
            run = run.MarkRunning(T0);
            run = run.MarkWaitingDelay(T0.AddMinutes(5), T0);
        }
        else
        {
            run = run.MarkRunning(T0);
            run = status switch
            {
                RunStatus.Running => run,
                RunStatus.Completed => run.MarkCompleted(T0),
                RunStatus.Failed => run.MarkFailed(T0, "E_FAIL"),
                RunStatus.Cancelled => run.ApplyCancellation(T0),
                RunStatus.WaitingApproval => run.MarkWaitingApproval(T0),
                RunStatus.BlockedPolicy => run.MarkBlockedPolicy(T0, "E_BLOCK"),
                RunStatus.NeedsReview => run.ExpireToNeedsReview(T0),
                _ => run
            };
        }

        if (started.HasValue) run = run with { StartedAtUtc = started };
        return run;
    }

    public static StepRun MakeStep(IIdGenerator ids, RunId runId, string nodeId, string nodeKind,
        DateTimeOffset? started = null, string? output = null) =>
        StepRun.Create(StepRunId.Create(ids), runId, nodeId, nodeKind, "idem-key", "input-digest",
            startedAtUtc: started, outputSummaryJson: output);

    public static RunEvent MakeEvent(IIdGenerator ids, RunId runId, StepRunId? stepId, string code,
        string props, int seq, DateTimeOffset at)
    {
        var ev = RunEvent.Create(RunEventId.Create(ids), runId, "run.event", RunEventLevel.Info,
            code, code, props, runId.Value, at, stepId, null);
        return ev.WithSequence(seq);
    }

    public static RunWithDetails MakeDetails(AutomationRun run, RunSnapshot snap,
        IReadOnlyList<StepRun> steps, IReadOnlyList<RunEvent> events) =>
        new(run, snap, steps, events);
}
