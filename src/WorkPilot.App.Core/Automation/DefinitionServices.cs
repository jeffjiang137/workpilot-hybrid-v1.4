using WorkPilot.Application.Automation;
using WorkPilot.Application.Automation.Definition;
using WorkPilot.Application.Automation.Run;
using WorkPilot.Application.Automation.Run.Executors;
using WorkPilot.Application.Permission.Policy;
using WorkPilot.Contracts.Primitives;

namespace WorkPilot.App.Core.Automation;

/// <summary>
/// Composition root for the T22 definition lifecycle (export / import / dry-run / enable preflight).
/// Wires the T22a export/import ports, the T22b dry-run planner (through the composite
/// <see cref="NodeEffectExecutor"/>, with the capability executor left null so no permit/adapter
/// is ever touched), and the T22c preflight (through the live <see cref="PolicyProjectionService"/>
/// — the same pure evaluator the real gate uses). Host-specific backends/sink/store are injected;
/// the resulting <see cref="IDefinitionManager"/> is consumed by <see cref="DefinitionManagerViewModel"/>.
/// </summary>
public static class DefinitionServices
{
    public static IDefinitionManager BuildManager(
        IAutomationRepository repo,
        IIdGenerator ids,
        IClock clock,
        IAgentBackend agentBackend,
        INotificationSink notificationSink,
        IPolicyStore policyStore,
        PolicyEvaluationCache policyCache)
    {
        var exporter = new DefinitionExporter(repo, clock);
        var importer = new DefinitionImporter(repo, ids, clock);

        // Capability executor intentionally null: dry-run short-circuits every executor, so no
        // permit is issued and no adapter / sink / model backend is ever touched.
        var nodeExecutor = new NodeEffectExecutor(agentBackend, notificationSink);
        var planner = new DryRunPlanner(ids, clock, nodeExecutor);

        var simulator = new PolicySimulatorService(policyStore);
        var projection = new PolicyProjectionService(simulator);
        var preflight = new EnablePreflightService(projection);

        return new DefinitionOrchestration(exporter, importer, planner, preflight, repo);
    }
}
