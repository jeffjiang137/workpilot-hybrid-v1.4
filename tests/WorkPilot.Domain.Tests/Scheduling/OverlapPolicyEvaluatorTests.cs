using System.Collections.Generic;
using System.Linq;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation;
using WorkPilot.Domain.Automation.Scheduling;
using Xunit;

namespace WorkPilot.Domain.Tests.Scheduling;

public sealed class OverlapPolicyEvaluatorTests
{
    private static ExistingRunSummary Run(string id, RunStatusCategory status, bool cancel = false) =>
        new(RunId.Parse(id), status, cancel);

    // RUN-A05: skip policy suppresses a new run while one is in-flight.
    [Fact]
    public void Skip_suppresses_when_in_flight()
    {
        var existing = new[] { Run("run_1", RunStatusCategory.Active) };
        Assert.Equal(OverlapDecisionKind.Skip, OverlapPolicyEvaluator.Evaluate(OverlapPolicy.Skip, existing).Kind);
        Assert.Equal(OverlapDecisionKind.Create, OverlapPolicyEvaluator.Evaluate(OverlapPolicy.Skip, System.Array.Empty<ExistingRunSummary>()).Kind);
    }

    // RUN-A06: cancel_previous requests cancellation of in-flight runs and still creates.
    [Fact]
    public void CancelPrevious_requests_cancellation_and_creates()
    {
        var existing = new[] { Run("run_1", RunStatusCategory.Active), Run("run_2", RunStatusCategory.Queued) };
        var d = OverlapPolicyEvaluator.Evaluate(OverlapPolicy.CancelPrevious, existing);
        Assert.Equal(OverlapDecisionKind.CancelPreviousAndCreate, d.Kind);
        Assert.Equal(2, d.CancellationTargetIds!.Count);
        Assert.Contains(RunId.Parse("run_1"), d.CancellationTargetIds!);
        Assert.Contains(RunId.Parse("run_2"), d.CancellationTargetIds!);
    }

    [Fact]
    public void CancelPrevious_creates_when_nothing_in_flight()
    {
        Assert.Equal(OverlapDecisionKind.Create,
            OverlapPolicyEvaluator.Evaluate(OverlapPolicy.CancelPrevious, System.Array.Empty<ExistingRunSummary>()).Kind);
    }

    // RUN-A04: queue_one keeps at most one queued run; 20 candidates coalesce into it (coalesced=19).
    [Fact]
    public void QueueOne_coalesces_twenty_candidates_into_one_queued_run()
    {
        var runs = new List<ExistingRunSummary> { Run("run_active", RunStatusCategory.Active) };
        RunId? queuedId = null;
        var absorbed = 0; // candidates merged into the single queued run (excludes the creating candidate)

        for (var i = 1; i <= 20; i++)
        {
            var decision = OverlapPolicyEvaluator.Evaluate(
                OverlapPolicy.QueueOne, runs.ToArray(),
                queuedId is { } q ? absorbed + 1 : 1);
            if (decision.Kind == OverlapDecisionKind.Create)
            {
                queuedId = RunId.Parse($"run_q{i}");
                runs.Add(Run(queuedId.Value.Value, RunStatusCategory.Queued));
                // creating candidate is the 1st; nothing absorbed yet
                absorbed = 0;
            }
            else if (decision.Kind == OverlapDecisionKind.Coalesce && decision.CoalesceTargetId is { } target)
            {
                Assert.Equal(queuedId, target); // every coalesce targets the single queued run
                absorbed++; // this candidate is merged into the queued run
            }
            else
            {
                Assert.Fail($"Unexpected decision {decision.Kind} on candidate {i}");
            }
        }

        var queued = runs.Where(r => r.Status == RunStatusCategory.Queued).ToList();
        Assert.Single(queued);   // at most one queued run
        Assert.Equal(19, absorbed); // 20 candidates - 1 creating = 19 coalesced
    }
}
