using System.Collections.Generic;
using WorkPilot.Application.Permission.Policy;
using WorkPilot.Domain.Automation.Validation;
using WorkPilot.Domain.PermissionGovernance.Evaluation;

namespace WorkPilot.Application.Automation.Definition;

/// <summary>
/// Outcome of an enable Preflight (AUT-005 / AUT-A10). <see cref="CanEnable"/> is true only when
/// there are NO errors (warnings are allowed — they are surfaced for the operator). <see cref="Validation"/>
/// is the aggregated, sorted, JSON-Pointer-located issue set (structure + binding + budget + capability).
/// <see cref="CapabilityDecisions"/> carries the per-capability effective decisions (kind / risk / scope /
/// trace) so the UI can show "what this automation can actually do" alongside the blockers.
/// </summary>
public sealed record PreflightResult(
    bool CanEnable,
    ValidationResult Validation,
    IReadOnlyList<EffectiveCapabilityView> CapabilityDecisions)
{
    public IReadOnlyList<ValidationIssue> Errors => Validation.Errors;
    public IReadOnlyList<ValidationIssue> Warnings => Validation.Warnings;

    /// <summary>A preflight that could not even load the revision (storage/identity failure).</summary>
    public static PreflightResult Failed(string code) =>
        new(false, new ValidationResult(new[] { PreflightCodes.Error(code, "/") }),
            Array.Empty<EffectiveCapabilityView>());
}
