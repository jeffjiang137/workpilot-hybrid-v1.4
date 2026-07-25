using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using WorkPilot.Application.Automation.Run.Executors;
using WorkPilot.Domain.Automation;
using WorkPilot.Domain.Automation.Run;
using WorkPilot.Domain.Automation.Run.Interpreter;
using Xunit;

namespace WorkPilot.Application.Tests.Executors;

/// <summary>Delay node (doc 03 §3.5 / RUN-004): computes resume_at_utc, consumes no budget.</summary>
public class DelayExecutorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    private static VariableStore VarsWithClock()
        => new(systemVars: new Dictionary<string, JsonNode> { ["now"] = JsonValue.Create(Now.ToString("O")) });

    private static WorkflowNode Delay(long seconds)
        => new("d", "delay", "delay", 60, false, new JsonObject { ["delay_seconds"] = seconds });

    [Fact]
    public void Delay_computes_resume_time_and_releases_slot()
    {
        var exec = new DelayExecutor();
        var result = exec.ExecuteNode(Delay(600), VarsWithClock(), RunFakes.CapabilityRun(Now), RunFakes.DummyStep("d"), CancellationToken.None);

        Assert.Equal(StepRunStatus.WaitingDelay, result.Status);
        Assert.Equal(Now.AddSeconds(600), result.ResumeAtUtc);
    }

    [Fact]
    public void Delay_estimate_consumes_no_budget()
    {
        var cost = new DelayExecutor().Estimate(Delay(600));
        Assert.Equal(0, cost.ModelTurns);
        Assert.Equal(0, cost.CapabilityCalls);
        Assert.Equal(0, cost.ResultBytes);
        Assert.Equal(0, cost.WallClockSeconds);
    }

    [Fact]
    public void Delay_out_of_range_is_rejected_closed()
    {
        var exec = new DelayExecutor();
        var tooSmall = exec.ExecuteNode(Delay(10), VarsWithClock(), RunFakes.CapabilityRun(Now), RunFakes.DummyStep("d"), CancellationToken.None);
        var tooBig = exec.ExecuteNode(Delay(100_000), VarsWithClock(), RunFakes.CapabilityRun(Now), RunFakes.DummyStep("d"), CancellationToken.None);

        Assert.Equal(StepRunStatus.Failed, tooSmall.Status);
        Assert.Equal("RUN_DELAY_INVALID", tooSmall.ErrorCode);
        Assert.Equal(StepRunStatus.Failed, tooBig.Status);
        Assert.Equal("RUN_DELAY_INVALID", tooBig.ErrorCode);
    }

    [Fact]
    public void Delay_without_clock_fails_closed()
    {
        var exec = new DelayExecutor();
        var result = exec.ExecuteNode(Delay(600), new VariableStore(), RunFakes.CapabilityRun(Now), RunFakes.DummyStep("d"), CancellationToken.None);
        Assert.Equal(StepRunStatus.Failed, result.Status);
        Assert.Equal("RUN_DELAY_CLOCK_INVALID", result.ErrorCode);
    }
}
