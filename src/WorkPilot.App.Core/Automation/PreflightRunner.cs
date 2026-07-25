using System.Collections.Generic;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation;
using WorkPilot.Domain.Automation.Validation;

namespace WorkPilot.App.Core.Automation;

/// <summary>Coarse status of one preflight check. <see cref="NotEvaluated"/> is NOT a pass: it marks a
/// check whose backend is not yet wired (e.g. policy grant evaluation lands in T17). It never blocks save.</summary>
public enum PreflightStatus
{
    Error,
    Warning,
    Passed,
    NotEvaluated
}

/// <summary>Which aspect of the automation a preflight check covers.</summary>
public enum PreflightCategory
{
    Definition,
    Trigger,
    Workflow,
    Schema,
    Expert,
    Source,
    Grant,
    Budget,
    Host
}

/// <summary>A single structured preflight finding. Stable <see cref="Code"/> and <see cref="MessageKey"/> only.</summary>
public sealed record PreflightCheck(
    PreflightCategory Category,
    PreflightStatus Status,
    string Code,
    string MessageKey,
    string? JsonPointer = null,
    IReadOnlyDictionary<string, string>? SafeDetails = null);

/// <summary>The assembled automation the preflight evaluates.</summary>
public sealed record PreflightContext(
    string Name,
    SpaceId? SpaceId,
    string? ExpertId,
    TriggerDefinition Trigger,
    WorkflowDefinition Workflow);

/// <summary>
/// Aggregates preflight checks for the editor's "Test &amp; Enable" step (doc 02 §4.5). Reuses the SAME
/// T05 <see cref="TriggerValidator"/> / <see cref="WorkflowValidator"/> as saving and materialization.
/// Checks whose backend is not wired in this increment (schema validation, source health, policy grant
/// evaluation, background host availability) are reported as <see cref="PreflightStatus.NotEvaluated"/>
/// — never as a fake Passed (AI dev rule §16). Only <see cref="PreflightStatus.Error"/> blocks enable.
/// </summary>
public static class PreflightRunner
{
    public static IReadOnlyList<PreflightCheck> Run(PreflightContext ctx)
    {
        var checks = new List<PreflightCheck>();

        // Definition-level checks (name/space/expert are always computable now).
        var name = (ctx.Name ?? string.Empty).Trim();
        if (name.Length < 1 || name.Length > Limits.V1_5.MaxAutomationNameLength)
            checks.Add(PreflightCodes.Definition("PRE_NAME", "Preflight.Definition.NameInvalid"));
        if (ctx.SpaceId is null || ctx.SpaceId.Value == default)
            checks.Add(PreflightCodes.Definition("PRE_SPACE", "Preflight.Definition.SpaceMissing"));
        if (string.IsNullOrWhiteSpace(ctx.ExpertId))
            checks.Add(PreflightCodes.Definition("PRE_EXPERT", "Preflight.Definition.ExpertMissing"));

        // Trigger + Workflow: map the shared validators' issues 1:1.
        var trigger = TriggerValidator.Validate(ctx.Trigger);
        foreach (var issue in trigger.Issues)
            checks.Add(ToCheck(PreflightCategory.Trigger, issue));

        var workflow = WorkflowValidator.Validate(ctx.Workflow);
        foreach (var issue in workflow.Issues)
            checks.Add(ToCheck(PreflightCategory.Workflow, issue));

        // Backend-dependent checks: honest "not evaluated yet", not a silent pass.
        checks.Add(PreflightCodes.Pending(PreflightCategory.Schema, "PRE_SCHEMA_PENDING", "Preflight.Schema.Pending"));
        checks.Add(PreflightCodes.Pending(PreflightCategory.Expert, "PRE_EXPERT_PENDING", "Preflight.Expert.Pending"));
        checks.Add(PreflightCodes.Pending(PreflightCategory.Source, "PRE_SOURCE_PENDING", "Preflight.Source.Pending"));
        checks.Add(PreflightCodes.Pending(PreflightCategory.Grant, "PRE_GRANT_PENDING", "Preflight.Grant.Pending"));
        checks.Add(PreflightCodes.Pending(PreflightCategory.Budget, "PRE_BUDGET_PENDING", "Preflight.Budget.Pending"));
        checks.Add(PreflightCodes.Pending(PreflightCategory.Host, "PRE_HOST_PENDING", "Preflight.Host.Pending"));

        return checks;
    }

    public static bool HasErrors(IReadOnlyList<PreflightCheck> checks) =>
        checks.Any(c => c.Status == PreflightStatus.Error);

    public static bool HasWarnings(IReadOnlyList<PreflightCheck> checks) =>
        checks.Any(c => c.Status == PreflightStatus.Warning);

    private static PreflightCheck ToCheck(PreflightCategory category, ValidationIssue issue) =>
        new(category,
            issue.Severity == ValidationSeverity.Error ? PreflightStatus.Error : PreflightStatus.Warning,
            issue.Code, issue.MessageKey, issue.JsonPointer, issue.SafeDetails);
}

/// <summary>Stable codes/message keys for editor preflight checks (no inline literals).</summary>
public static class PreflightCodes
{
    public static PreflightCheck Definition(string code, string messageKey) =>
        new(PreflightCategory.Definition, PreflightStatus.Error, code, messageKey);

    public static PreflightCheck Pending(PreflightCategory category, string code, string messageKey) =>
        new(category, PreflightStatus.NotEvaluated, code, messageKey);
}
