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

/// <summary>Notification node (doc 03 §3.6 / RUN-008): only run metadata and explicitly-safe
/// <c>vars.*</c> keys may be referenced; secrets and free-text model output must never reach the sink.</summary>
public class NotificationExecutorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    private static VariableStore Store()
    {
        var s = new VariableStore(
            triggerVars: new Dictionary<string, JsonNode> { ["owner"] = JsonValue.Create("alice") },
            runVars: new Dictionary<string, JsonNode>
            {
                ["id"] = JsonValue.Create("run_1"),
                ["priority"] = JsonValue.Create(1),
                ["trigger_kind"] = JsonValue.Create("interval")
            },
            systemVars: new Dictionary<string, JsonNode> { ["now"] = JsonValue.Create(Now.ToString("O")) });
        // An upstream agent produced a safe count and a confidential full-text output.
        s.Declare("a", "count", JsonValue.Create(5));
        s.Declare("a", "full_text", JsonValue.Create("CONFIDENTIAL MODEL OUTPUT"));
        return s;
    }

    private static WorkflowNode Notification(string template, string[] safeKeys, bool required = false, string? title = null)
    {
        var payload = new JsonObject { ["template"] = template, ["required"] = required };
        if (title is not null) payload["title"] = title;
        var arr = new JsonArray();
        foreach (var k in safeKeys) arr.Add(k);
        payload["safe_output_keys"] = arr;
        return new WorkflowNode("n", "notify", "notification", 60, false, payload);
    }

    [Fact]
    public void Safe_notification_renders_and_delivers()
    {
        var node = Notification("Run {{$ref:run.id}} count {{$ref:vars.count}}", new[] { "count" });
        var sink = new RecordingNotificationSink();
        var result = new NotificationExecutor(sink).ExecuteNode(node, Store(), RunFakes.CapabilityRun(Now), RunFakes.DummyStep("n"), CancellationToken.None);

        Assert.Equal(StepRunStatus.Succeeded, result.Status);
        Assert.Equal("Run run_1 count 5", sink.Last!.Body);
        Assert.True(sink.Last.Body.Length <= 200);
        Assert.Equal("WorkPilot 自动化", sink.Last.Title);
    }

    [Fact]
    public void Confidential_model_output_cannot_leak_into_notification()
    {
        // full_text is NOT in safe_output_keys, so referencing it must fail closed (never reach the sink).
        var node = Notification("Output: {{$ref:vars.full_text}}", new[] { "count" });
        var sink = new RecordingNotificationSink();
        var result = new NotificationExecutor(sink).ExecuteNode(node, Store(), RunFakes.CapabilityRun(Now), RunFakes.DummyStep("n"), CancellationToken.None);

        Assert.Equal(StepRunStatus.Failed, result.Status);
        Assert.Equal("RUN_NOTIFICATION_RENDER", result.ErrorCode);
        Assert.Null(sink.Last); // sink never received the confidential text
    }

    [Fact]
    public void Trigger_payload_cannot_leak_into_notification()
    {
        // trigger.* is not on the notification allow-list, even though it is generally resolvable.
        var node = Notification("Owner {{$ref:trigger.owner}}", new[] { "count" });
        var sink = new RecordingNotificationSink();
        var result = new NotificationExecutor(sink).ExecuteNode(node, Store(), RunFakes.CapabilityRun(Now), RunFakes.DummyStep("n"), CancellationToken.None);

        Assert.Equal(StepRunStatus.Failed, result.Status);
        Assert.Equal("RUN_NOTIFICATION_RENDER", result.ErrorCode);
        Assert.Null(sink.Last);
    }

    [Fact]
    public void Body_is_truncated_to_200_characters()
    {
        var store = Store();
        store.Declare("a", "longval", JsonValue.Create(new string('x', 500)));
        var node = Notification("{{$ref:vars.longval}}", new[] { "longval" });
        var sink = new RecordingNotificationSink();
        new NotificationExecutor(sink).ExecuteNode(node, store, RunFakes.CapabilityRun(Now), RunFakes.DummyStep("n"), CancellationToken.None);

        Assert.Equal(200, sink.Last!.Body.Length);
    }

    [Fact]
    public void Required_notification_failure_fails_the_run()
    {
        var sink = new RecordingNotificationSink { ShouldDeliver = false };
        var node = Notification("hi", Array.Empty<string>(), required: true);
        var result = new NotificationExecutor(sink).ExecuteNode(node, Store(), RunFakes.CapabilityRun(Now), RunFakes.DummyStep("n"), CancellationToken.None);

        Assert.Equal(StepRunStatus.Failed, result.Status);
        Assert.Equal("RUN_NOTIFICATION_FAILED", result.ErrorCode);
    }

    [Fact]
    public void Optional_notification_failure_does_not_fail_the_run()
    {
        var sink = new RecordingNotificationSink { ShouldDeliver = false };
        var node = Notification("hi", Array.Empty<string>()); // required defaults to false
        var result = new NotificationExecutor(sink).ExecuteNode(node, Store(), RunFakes.CapabilityRun(Now), RunFakes.DummyStep("n"), CancellationToken.None);

        Assert.Equal(StepRunStatus.Succeeded, result.Status);
    }
}
