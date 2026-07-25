using System.Collections.Generic;
using System.Text.Json.Nodes;
using WorkPilot.Domain.Automation.Run;
using WorkPilot.Domain.Automation.Run.Interpreter;

namespace WorkPilot.Application.Automation.Run;

/// <summary>One planned step produced by a dry-run simulation (RUN-005 / AUT-A11).</summary>
public sealed record DryRunStepPlan(
    string NodeId,
    string NodeKind,
    StepRunStatus Status,
    JsonNode? PlanSummary)
{
    /// <summary>True when this step would perform an external side effect (capability write / notification).</summary>
    public bool IsSideEffecting => NodeKind is "capability_call" or "notification";
}

/// <summary>
/// Result of a dry-run simulation over an automation revision. No I/O is performed — every executor
/// short-circuits on <see cref="WorkPilot.Domain.Automation.Run.AutomationRun.IsDryRun"/> — so this is
/// a pure description of what WOULD happen if the automation were enabled and run for real.
/// <see cref="RealSendCount"/> is always 0 by construction.
/// </summary>
public sealed record DryRunPlan(
    bool IsValid,
    string? FinalStatus,
    IReadOnlyList<DryRunStepPlan> Steps,
    bool WouldSendSideEffects,
    int PlannedSideEffectCount,
    int RealSendCount,
    string? ErrorCode)
{
    public static DryRunPlan Invalid(string errorCode) =>
        new(false, null, System.Array.Empty<DryRunStepPlan>(), false, 0, 0, errorCode);
}
