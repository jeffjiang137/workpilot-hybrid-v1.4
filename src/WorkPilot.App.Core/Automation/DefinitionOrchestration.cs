using System;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Application.Automation;
using WorkPilot.Application.Automation.Definition;
using WorkPilot.Application.Automation.Run;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation;
using WorkPilot.Domain.PermissionGovernance.Evaluation;

namespace WorkPilot.App.Core.Automation;

/// <summary>
/// Application-layer implementation of the BCL <see cref="IDefinitionManager"/> seam (T22). It
/// composes the T22a export/import ports, the T22b dry-run planner, and the T22c enable
/// preflight into the four lifecycle operations the UI/BCL calls. For dry-run and preflight it
/// loads the automation's CURRENT revision from the repository, then delegates to the pure planners.
/// (BCL hosts the composition root because BCL references Application; Application intentionally
/// does not reference BCL — see T02 layering.)
/// </summary>
public sealed class DefinitionOrchestration : IDefinitionManager
{
    private readonly IDefinitionExporter _exporter;
    private readonly IDefinitionImporter _importer;
    private readonly DryRunPlanner _planner;
    private readonly EnablePreflightService _preflight;
    private readonly IAutomationRepository _repo;

    public DefinitionOrchestration(
        IDefinitionExporter exporter,
        IDefinitionImporter importer,
        DryRunPlanner planner,
        EnablePreflightService preflight,
        IAutomationRepository repo)
    {
        _exporter = exporter ?? throw new ArgumentNullException(nameof(exporter));
        _importer = importer ?? throw new ArgumentNullException(nameof(importer));
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _preflight = preflight ?? throw new ArgumentNullException(nameof(preflight));
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
    }

    public Task<Result<DefinitionExport>> ExportAsync(AutomationId id, CancellationToken ct = default)
        => _exporter.ExportAsync(id, ct);

    public Task<Result<ImportedAutomation>> ImportAsync(string json, CancellationToken ct = default)
        => _importer.ImportAsync(json, ct);

    public async Task<DryRunPlan> DryRunAsync(AutomationId id, CancellationToken ct = default)
    {
        var def = await _repo.GetAsync(id, ct).ConfigureAwait(false);
        if (!def.IsSuccess) return DryRunPlan.Invalid(def.Error!.Code);
        var rev = await _repo.GetRevisionAsync(def.Value!.CurrentRevisionId, ct).ConfigureAwait(false);
        if (!rev.IsSuccess) return DryRunPlan.Invalid(rev.Error!.Code);
        return _planner.Plan(rev.Value!, ct);
    }

    public async Task<PreflightResult> PreflightAsync(AutomationId id, EvaluationContext context, CancellationToken ct = default)
    {
        var def = await _repo.GetAsync(id, ct).ConfigureAwait(false);
        if (!def.IsSuccess) return PreflightResult.Failed(def.Error!.Code);
        var rev = await _repo.GetRevisionAsync(def.Value!.CurrentRevisionId, ct).ConfigureAwait(false);
        if (!rev.IsSuccess) return PreflightResult.Failed(rev.Error!.Code);
        return await _preflight.RunAsync(rev.Value!, context, ct).ConfigureAwait(false);
    }
}
