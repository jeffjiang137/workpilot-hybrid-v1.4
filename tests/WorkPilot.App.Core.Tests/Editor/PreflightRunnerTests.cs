using System;
using WorkPilot.App.Core.Automation;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation;
using Xunit;

namespace WorkPilot.App.Core.Tests.Editor;

public class PreflightRunnerTests
{
    private static DateTimeOffset Now => new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static TriggerDefinition ValidInterval() =>
        new("t", TriggerType.Interval, true, "UTC", null, null, 3600, Now, null, null, null, null, null, null);

    private static WorkflowDefinition ValidWorkflow() =>
        new(1, "n1", new[] { new WorkflowNode("n1", "S1", "agent_prompt", 5, false, null) },
            System.Array.Empty<WorkflowEdge>());

    [Fact]
    public void Missing_name_is_an_error()
    {
        var checks = PreflightRunner.Run(new PreflightContext("", SpaceId.Parse("s"), "e", ValidInterval(), ValidWorkflow()));
        Assert.Contains(checks, c => c.Code == "PRE_NAME" && c.Status == PreflightStatus.Error);
    }

    [Fact]
    public void Missing_space_is_an_error()
    {
        var checks = PreflightRunner.Run(new PreflightContext("ok", null, "e", ValidInterval(), ValidWorkflow()));
        Assert.Contains(checks, c => c.Code == "PRE_SPACE" && c.Status == PreflightStatus.Error);
    }

    [Fact]
    public void Missing_expert_is_an_error()
    {
        var checks = PreflightRunner.Run(new PreflightContext("ok", SpaceId.Parse("s"), null, ValidInterval(), ValidWorkflow()));
        Assert.Contains(checks, c => c.Code == "PRE_EXPERT" && c.Status == PreflightStatus.Error);
    }

    [Fact]
    public void Valid_definition_trigger_and_workflow_have_no_errors()
    {
        var checks = PreflightRunner.Run(new PreflightContext("ok", SpaceId.Parse("s"), "e", ValidInterval(), ValidWorkflow()));
        Assert.DoesNotContain(checks, c => c.Category == PreflightCategory.Definition && c.Status == PreflightStatus.Error);
        Assert.DoesNotContain(checks, c => c.Category == PreflightCategory.Trigger && c.Status == PreflightStatus.Error);
        Assert.DoesNotContain(checks, c => c.Category == PreflightCategory.Workflow && c.Status == PreflightStatus.Error);
    }

    [Fact]
    public void Invalid_trigger_surfaces_a_trigger_error()
    {
        var bad = new TriggerDefinition("t", TriggerType.Interval, true, "UTC", null, null, 30, Now, null, null, null, null, null, null);
        var checks = PreflightRunner.Run(new PreflightContext("ok", SpaceId.Parse("s"), "e", bad, ValidWorkflow()));
        Assert.Contains(checks, c => c.Category == PreflightCategory.Trigger && c.Status == PreflightStatus.Error);
    }

    [Fact]
    public void Backend_dependent_checks_are_honestly_NotEvaluated_never_Passed()
    {
        var checks = PreflightRunner.Run(new PreflightContext("ok", SpaceId.Parse("s"), "e", ValidInterval(), ValidWorkflow()));
        // No fake "Passed" may ever appear in the preflight.
        Assert.DoesNotContain(checks, c => c.Status == PreflightStatus.Passed);
        // The not-yet-wired backend checks are explicitly NotEvaluated.
        Assert.Contains(checks, c => c.Category == PreflightCategory.Grant && c.Status == PreflightStatus.NotEvaluated);
        Assert.Contains(checks, c => c.Category == PreflightCategory.Host && c.Status == PreflightStatus.NotEvaluated);
        Assert.Contains(checks, c => c.Category == PreflightCategory.Schema && c.Status == PreflightStatus.NotEvaluated);
    }
}
