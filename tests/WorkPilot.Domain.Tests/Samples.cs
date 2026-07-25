using System;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation;

namespace WorkPilot.Domain.Tests;

internal static class Samples
{
    public static readonly DateTimeOffset FixedNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static TriggerDefinition IntervalTrigger(long intervalSeconds = 3600) => new(
        "interval_1", TriggerType.Interval, true, null, null, null, intervalSeconds,
        FixedNow, null, null, null, null, null, null);

    public static WorkflowDefinition SingleAgent() => new(1, "agent_prompt_1",
        new[] { new WorkflowNode("agent_prompt_1", "指令", "agent_prompt", 60, false, null) },
        Array.Empty<WorkflowEdge>());

    public static AutomationBinding Binding() => new(null, null);

    public static RunBudget Budget(int maxModelTurns = 8, long maxTokens = 200_000) =>
        new(maxModelTurns, maxTokens, 3600, 100, 10_000_000);

    public static PermissionRequest Permission() => new(Array.Empty<string>(), "read-only");

    public static (AutomationRevision revision, AutomationRevisionId id) MakeRevision(
        AutomationId automationId, int number, RunBudget? budget = null, string? scope = null)
    {
        var id = AutomationRevisionId.Parse($"rev_{number}");
        var revision = AutomationRevision.Create(id, automationId, number,
            IntervalTrigger(), SingleAgent(), Binding(), budget ?? Budget(),
            OverlapPolicy.Skip, MissedRunPolicy.RunOnce,
            new PermissionRequest(Array.Empty<string>(), scope ?? "read-only"), FixedNow);
        return (revision, id);
    }
}
