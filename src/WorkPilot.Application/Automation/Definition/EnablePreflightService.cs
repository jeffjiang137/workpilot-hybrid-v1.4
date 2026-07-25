using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Application.Permission.Policy;
using WorkPilot.Domain.Automation;
using WorkPilot.Domain.Automation.Validation;
using WorkPilot.Domain.PermissionGovernance;
using WorkPilot.Domain.PermissionGovernance.Evaluation;

namespace WorkPilot.Application.Automation.Definition;

/// <summary>
/// Enable Preflight (AUT-005 / AUT-A10): a single, side-effect-free gate run BEFORE an automation
/// is enabled. It composes three independent pre-checks and aggregates them into one
/// <see cref="PreflightResult"/>:
/// <list type="bullet">
///   <item><description><b>Structure</b> — the T05 <see cref="WorkflowValidator"/> and
///     <see cref="TriggerValidator"/> (the same pure rules the editor and materializer use).</description></item>
///   <item><description><b>Binding / budget</b> — the automation must be bound to a space and carry a
///     within-limits run budget.</description></item>
///   <item><description><b>Permission</b> — every <c>capability_call</c> node is evaluated against the
///     LIVE policy with the SAME pure evaluator the real gate uses (doc 07 §14), via
///     <see cref="ICapabilityPermissionProbe"/>. A capability that would be <c>Deny</c> blocks
///     enabling (an automation that can never run should not be enabled); <c>Ask</c>/<c>Defer</c>
///     surface as warnings (runs will require approval / are temporarily gated).</description></item>
/// </list>
/// Any <see cref="ValidationSeverity.Error"/> makes <see cref="PreflightResult.CanEnable"/> false.
/// Warnings never block — they are surfaced for the operator.
/// </summary>
public sealed class EnablePreflightService
{
    // Budget bounds mirror the import validator (DefinitionSchemaValidator) so authoring and preflight
    // agree on what "within limits" means. These are preflight-specific constants.
    private const long MinWallSeconds = 60;
    private const long MaxWallSeconds = 3600;
    private const int MinModelTurns = 1;
    private const int MaxModelTurns = 32;
    private const int MinCapabilityCalls = 0;
    private const int MaxCapabilityCalls = 100;
    private const long MinResultBytes = 1024;
    private const long MaxResultBytes = 1_048_576;
    private const long MinTotalTokens = 1;
    private const long MaxTotalTokens = 1_048_576;

    private readonly ICapabilityPermissionProbe _probe;

    public EnablePreflightService(ICapabilityPermissionProbe probe)
        => _probe = probe ?? throw new ArgumentNullException(nameof(probe));

    /// <summary>
    /// Runs the full preflight for a revision. <paramref name="context"/> is the caller-supplied,
    /// point-in-time policy context (source enabled / space linked / grant present / epoch / emergency /
    /// clock) — normally built by the BCL/UI from current session state. <see cref="PolicySubject"/>
    /// should be <see cref="PolicySubject.AutomationPrincipal"/> because runs execute as the automation.
    /// </summary>
    public async Task<PreflightResult> RunAsync(
        AutomationRevision revision,
        EvaluationContext context,
        CancellationToken ct = default)
    {
        var issues = new List<ValidationIssue>();

        // 1) Structure (T05) — workflow + trigger
        issues.AddRange(WorkflowValidator.Validate(revision.Workflow).Issues);
        issues.AddRange(TriggerValidator.Validate(revision.Trigger).Issues);

        // 2) Binding / source
        ValidateBinding(revision.Binding, issues);

        // 3) Budget
        ValidateBudget(revision.Budget, issues);

        // 4) Permission pre-check per capability_call node (T17, live policy)
        var queries = ExtractCapabilityQueries(revision.Workflow, issues);
        IReadOnlyList<EffectiveCapabilityView> decisions = Array.Empty<EffectiveCapabilityView>();
        if (queries.Count > 0)
        {
            decisions = await _probe.ProjectAsync(context, queries.Select(x => x.Query).ToList(), ct)
                .ConfigureAwait(false);
            foreach (var d in decisions)
            {
                var entry = queries.FirstOrDefault(q => q.Query.StableId == d.CapabilityStableId);
                var ptr = entry.NodeId is not null
                    ? $"/workflow/nodes/{entry.NodeId}"
                    : $"/workflow/capabilities/{d.CapabilityStableId}";
                switch (d.Decision)
                {
                    case PermissionDecisionKind.Deny:
                        issues.Add(PreflightCodes.Error(PreflightCodes.CapabilityDenied, ptr,
                            ("capability", d.CapabilityStableId), ("reason", d.PrimaryReasonCode)));
                        break;
                    case PermissionDecisionKind.Ask:
                        issues.Add(PreflightCodes.Warning(PreflightCodes.CapabilityAsk, ptr,
                            ("capability", d.CapabilityStableId), ("reason", d.PrimaryReasonCode)));
                        break;
                    case PermissionDecisionKind.Defer:
                        issues.Add(PreflightCodes.Warning(PreflightCodes.CapabilityDeferred, ptr,
                            ("capability", d.CapabilityStableId), ("reason", d.PrimaryReasonCode)));
                        break;
                }
            }
        }

        var merged = new ValidationResult(issues);
        return new PreflightResult(merged.IsValid, merged, decisions);
    }

    private static void ValidateBinding(AutomationBinding binding, List<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(binding.ExpertId))
            issues.Add(PreflightCodes.Warning(PreflightCodes.BindingExpertMissing, "/binding/expert_id"));
        // Space binding is mandatory: an automation with no space cannot be scheduled or run.
        // (AutomationBinding has no SpaceId field; space linkage is enforced by the host at enable
        // time. We surface a structural warning if the binding is otherwise empty.)
        if (string.IsNullOrWhiteSpace(binding.ExpertId) && string.IsNullOrWhiteSpace(binding.ProjectId))
            issues.Add(PreflightCodes.Error(PreflightCodes.BindingSpaceMissing, "/binding"));
    }

    private static void ValidateBudget(RunBudget budget, List<ValidationIssue> issues)
    {
        if (budget.MaxWallClockSeconds < MinWallSeconds || budget.MaxWallClockSeconds > MaxWallSeconds)
            issues.Add(PreflightCodes.Error(PreflightCodes.BudgetWallInvalid, "/budget/wall_clock_seconds",
                ("value", budget.MaxWallClockSeconds.ToString()), ("min", MinWallSeconds.ToString()), ("max", MaxWallSeconds.ToString())));
        if (budget.MaxModelTurns < MinModelTurns || budget.MaxModelTurns > MaxModelTurns)
            issues.Add(PreflightCodes.Error(PreflightCodes.BudgetTurnsInvalid, "/budget/model_turns",
                ("value", budget.MaxModelTurns.ToString()), ("min", MinModelTurns.ToString()), ("max", MaxModelTurns.ToString())));
        if (budget.MaxCapabilityCalls < MinCapabilityCalls || budget.MaxCapabilityCalls > MaxCapabilityCalls)
            issues.Add(PreflightCodes.Error(PreflightCodes.BudgetCapsInvalid, "/budget/capability_calls",
                ("value", budget.MaxCapabilityCalls.ToString()), ("min", MinCapabilityCalls.ToString()), ("max", MaxCapabilityCalls.ToString())));
        if (budget.MaxResultBytes < MinResultBytes || budget.MaxResultBytes > MaxResultBytes)
            issues.Add(PreflightCodes.Error(PreflightCodes.BudgetBytesInvalid, "/budget/result_bytes",
                ("value", budget.MaxResultBytes.ToString()), ("min", MinResultBytes.ToString()), ("max", MaxResultBytes.ToString())));
        if (budget.MaxTotalTokens < MinTotalTokens || budget.MaxTotalTokens > MaxTotalTokens)
            issues.Add(PreflightCodes.Error(PreflightCodes.BudgetTokensInvalid, "/budget/total_tokens",
                ("value", budget.MaxTotalTokens.ToString()), ("min", MinTotalTokens.ToString()), ("max", MaxTotalTokens.ToString())));
    }

    /// <summary>
    /// Walks the workflow for <c>capability_call</c> nodes and builds a <see cref="CapabilityQuery"/>
    /// per node. A node missing its capability identity is recorded as an Error (not silently skipped).
    /// </summary>
    private static List<(string? NodeId, CapabilityQuery Query)> ExtractCapabilityQueries(
        WorkflowDefinition workflow, List<ValidationIssue> issues)
    {
        var queries = new List<(string?, CapabilityQuery)>();
        foreach (var node in workflow.Nodes)
        {
            if (node.Kind != "capability_call") continue;
            var cap = node.Payload?["capability"] as JsonObject;
            var stableId = cap is not null && cap["stable_id"] is JsonValue sv ? sv.GetValue<string>() : null;
            var sourceKind = cap is not null && cap["source_kind"] is JsonValue kv ? kv.GetValue<string>() : null;
            var sourceId = cap is not null && cap["source_id"] is JsonValue iv ? iv.GetValue<string>() : null;
            var schema = cap is not null && cap["schema_sha256"] is JsonValue shv ? shv.GetValue<string>() : null;
            var risk = cap is not null && cap["risk"] is JsonValue rv ? rv.GetValue<string>() : null;

            if (string.IsNullOrEmpty(stableId) || string.IsNullOrEmpty(sourceKind) || string.IsNullOrEmpty(sourceId))
            {
                issues.Add(PreflightCodes.Error(PreflightCodes.CapabilityIdentityMissing,
                    $"/workflow/nodes/{node.NodeId}/capability", ("node_id", node.NodeId)));
                continue;
            }

            var query = new CapabilityQuery(
                stableId!,
                schema ?? string.Empty,
                ParseRisk(risk),
                null); // invocation scope is unbounded for the pre-flight worst-case check
            queries.Add((node.NodeId, query));
        }
        return queries;
    }

    private static RiskLevel ParseRisk(string? risk) => risk?.ToLowerInvariant() switch
    {
        "low" => RiskLevel.Low,
        "medium" => RiskLevel.Medium,
        "high" => RiskLevel.High,
        "critical" => RiskLevel.Critical,
        _ => RiskLevel.Low
    };
}
