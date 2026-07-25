using System;
using System.Text.Json.Nodes;
using System.Threading;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Domain.Automation.Run;

namespace WorkPilot.Domain.Automation.Run.Interpreter;

/// <summary>Estimated four-dimension cost of executing a node, used for up-front budget reservation.</summary>
public sealed record NodeCost(int ModelTurns, int CapabilityCalls, long ResultBytes, long WallClockSeconds);

/// <summary>
/// Outcome of executing a single workflow node, produced by the (I/O-bearing) node executor that the
/// interpreter drives. The interpreter owns all scheduling/state-machine/budget logic; the executor
/// owns only the side effect and reports back a deterministic outcome. T11 supplies the real
/// executors (Agent/Capability/Delay/Notification); T10 tests use a scripted fake.
/// </summary>
public sealed record NodeEffectResult(
    StepRunStatus Status,
    string? OutputKey = null,
    JsonNode? OutputValue = null,
    string? ErrorCode = null,
    DateTimeOffset? ResumeAtUtc = null);

/// <summary>
/// Port to the side-effecting node executors (T11). The interpreter calls <see cref="Estimate"/> to
/// reserve budget up-front, then <see cref="ExecuteNode"/> to perform the side effect. Conditions are
/// evaluated inline by <see cref="ConditionEvaluator"/> and never reach this port. Keeping the port
/// behind an interface lets the pure interpreter be exercised in tests without any I/O, model, or scheduler.
/// </summary>
public interface INodeEffectExecutor
{
    /// <summary>Returns the cost the interpreter should reserve before executing <paramref name="node"/>.</summary>
    NodeCost Estimate(WorkflowNode node);

    /// <summary>
    /// Executes <paramref name="node"/> against the already-resolved <paramref name="inputVars"/> and
    /// returns a deterministic <see cref="NodeEffectResult"/>. The authoritative budget reservation
    /// has already happened in the interpreter via <see cref="Estimate"/>. The side-effecting executor
    /// receives the owning <paramref name="run"/> and the in-flight <paramref name="step"/> so it can
    /// bind a Native Permit and record the side-effect phase (T12).
    /// </summary>
    NodeEffectResult ExecuteNode(WorkflowNode node, VariableStore inputVars, AutomationRun run, StepRun step, CancellationToken ct);
}
