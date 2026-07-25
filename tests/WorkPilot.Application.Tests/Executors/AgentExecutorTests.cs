using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using WorkPilot.Application.Automation.Run.Executors;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Domain.Automation;
using WorkPilot.Domain.Automation.Run;
using WorkPilot.Domain.Automation.Run.Interpreter;
using Xunit;

namespace WorkPilot.Application.Tests.Executors;

/// <summary>Agent node (doc 03 §3.2 / RUN-004): builds a request from instruction + bindings, delegates
/// to <see cref="IAgentBackend"/>, maps cancellation and backend failure.</summary>
public class AgentExecutorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    private static VariableStore Store()
    {
        var s = new VariableStore(
            triggerVars: new Dictionary<string, JsonNode> { ["topic"] = JsonValue.Create("budget") },
            runVars: new Dictionary<string, JsonNode> { ["id"] = JsonValue.Create("run_1") });
        s.Declare("a", "summary", JsonValue.Create("done"));
        return s;
    }

    private static WorkflowNode Agent(string instruction, string[] bindings, string? outputKey = "result", long maxTurns = 2)
    {
        var payload = new JsonObject { ["instruction_template"] = instruction, ["max_model_turns"] = maxTurns, ["capability_mode"] = "none" };
        if (outputKey is null) payload["output_key"] = JsonValue.Create((string?)null);
        else payload["output_key"] = outputKey;
        var arr = new JsonArray();
        foreach (var b in bindings) arr.Add(b);
        payload["input_bindings"] = arr;
        return new WorkflowNode("a", "agent", "agent_prompt", 60, false, payload);
    }

    [Fact]
    public void Success_stores_output_and_resolves_bindings()
    {
        var backend = new ScriptedAgentBackend { NextResult = new(true, OutputValue: JsonValue.Create("RESULT"), ErrorCode: null) };
        var node = Agent("Summarize {{$ref:trigger.topic}}", new[] { "trigger.topic" }, "result");
        var result = new AgentExecutor(backend).ExecuteNode(node, Store(), RunFakes.CapabilityRun(Now), RunFakes.DummyStep("a"), CancellationToken.None);

        Assert.Equal(StepRunStatus.Succeeded, result.Status);
        Assert.Equal("result", result.OutputKey);
        Assert.Equal("\"RESULT\"", result.OutputValue!.ToJsonString());
        Assert.Equal("Summarize budget", backend.LastRequest!.Instruction);
        Assert.Equal(2, backend.LastRequest.MaxModelTurns);
    }

    [Fact]
    public void Backend_failure_fails_the_step()
    {
        var backend = new ScriptedAgentBackend { NextResult = new(false, ErrorCode: "BACKEND_DOWN") };
        var result = new AgentExecutor(backend).ExecuteNode(Agent("x", Array.Empty<string>()), Store(), RunFakes.CapabilityRun(Now), RunFakes.DummyStep("a"), CancellationToken.None);

        Assert.Equal(StepRunStatus.Failed, result.Status);
        Assert.Equal("RUN_AGENT_BACKEND", result.ErrorCode);
    }

    [Fact]
    public void Cancellation_propagates_as_OperationCanceled()
    {
        var backend = new ScriptedAgentBackend { ShouldCancel = true };
        var exec = new AgentExecutor(backend);
        Assert.Throws<OperationCanceledException>(() =>
            exec.ExecuteNode(Agent("x", Array.Empty<string>()), Store(), RunFakes.CapabilityRun(Now), RunFakes.DummyStep("a"), new CancellationToken(true)));
    }

    [Fact]
    public void Missing_instruction_fails_closed()
    {
        var node = new WorkflowNode("a", "agent", "agent_prompt", 60, false, new JsonObject());
        var result = new AgentExecutor(new ScriptedAgentBackend()).ExecuteNode(node, Store(), RunFakes.CapabilityRun(Now), RunFakes.DummyStep("a"), CancellationToken.None);

        Assert.Equal(StepRunStatus.Failed, result.Status);
        Assert.Equal("RUN_AGENT_INSTRUCTION", result.ErrorCode);
    }

    [Fact]
    public void Missing_input_binding_fails_closed()
    {
        var node = Agent("Use {{$ref:trigger.missing}}", new[] { "trigger.missing" }, "result");
        var result = new AgentExecutor(new ScriptedAgentBackend()).ExecuteNode(node, Store(), RunFakes.CapabilityRun(Now), RunFakes.DummyStep("a"), CancellationToken.None);

        Assert.Equal(StepRunStatus.Failed, result.Status);
        Assert.Equal("RUN_VAR_BINDING", result.ErrorCode);
    }

    [Fact]
    public void Estimate_clamps_model_turns_and_reads_timeout()
    {
        var exec = new AgentExecutor(new ScriptedAgentBackend());
        Assert.Equal(2, exec.Estimate(Agent("x", Array.Empty<string>(), maxTurns: 2)).ModelTurns);
        Assert.Equal(Limits.V1_5.MaxAgentModelTurns, exec.Estimate(Agent("x", Array.Empty<string>(), maxTurns: 99)).ModelTurns);
        Assert.Equal(60, exec.Estimate(Agent("x", Array.Empty<string>())).WallClockSeconds);
    }
}
