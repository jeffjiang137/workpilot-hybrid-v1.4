using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Application.Automation.Definition;
using WorkPilot.Application.Automation.Run;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.PermissionGovernance.Evaluation;

namespace WorkPilot.App.Core.Automation;

/// <summary>
/// BCL seam for the automation definition lifecycle (export / import / dry-run / enable preflight,
/// T22). The concrete implementation lives in the Application layer; the BCL view model depends only
/// on this interface so it is unit-testable on any platform without the WinUI host. All results are
/// non-secret read models (AUT-006 / RUN-005).
/// </summary>
public interface IDefinitionManager
{
    /// <summary>Export a single automation definition + its current revision as portable, non-secret JSON.</summary>
    Task<Result<DefinitionExport>> ExportAsync(AutomationId id, CancellationToken ct = default);

    /// <summary>Import a definition from portable JSON, rebuilding all identifiers (AUT-A07).</summary>
    Task<Result<ImportedAutomation>> ImportAsync(string json, CancellationToken ct = default);

    /// <summary>Plan one execution pass WITHOUT any side effect (RUN-005 / AUT-A11).</summary>
    Task<DryRunPlan> DryRunAsync(AutomationId id, CancellationToken ct = default);

    /// <summary>Run the enable Preflight (AUT-005 / AUT-A10) against the live policy.</summary>
    Task<PreflightResult> PreflightAsync(AutomationId id, EvaluationContext context, CancellationToken ct = default);
}
