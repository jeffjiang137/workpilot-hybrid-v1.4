using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation;
using WorkPilot.Domain.Automation.Run;
using WorkPilot.Domain.Automation.Run.Interpreter;

namespace WorkPilot.Application.Automation.Run;

/// <summary>Outcome of one interpreter pass driven through the Application layer.</summary>
public sealed record RunExecutionOutcome(
    AutomationRun Run,
    IReadOnlyList<StepRun> Steps,
    IReadOnlyList<RunEvent> Events,
    bool Halted);

/// <summary>
/// Application-layer driver for the pure <see cref="WorkflowInterpreter"/> (T10, AUT-004 / RUN-002/004).
/// Loads a claimed run and its frozen snapshot, rehydrates the workflow / budget / variable scope,
/// runs a single deterministic interpreter pass through an injected <see cref="INodeEffectExecutor"/>
/// (T11 supplies real Agent/Capability/Delay/Notification executors; T10 uses a scripted fake), then
/// atomically persists the run header, steps and emitted events. The interpreter itself performs no
/// I/O and never throws for expected halts (budget, cancellation, waits); only malformed snapshots or
/// storage failures surface as a failed <see cref="Result"/>.
/// </summary>
public sealed class WorkflowExecutionService
{
    private readonly IRunRepository _runs;
    private readonly INodeEffectExecutor _executor;
    private readonly IIdGenerator _ids;
    private readonly IClock _clock;

    public WorkflowExecutionService(IRunRepository runs, INodeEffectExecutor executor, IIdGenerator ids, IClock clock)
    {
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>
    /// Executes one interpreter pass over <paramref name="runId"/>. The run must be in
    /// <see cref="RunStatus.Claimed"/> or <see cref="RunStatus.Running"/>; any other state is rejected
    /// with a stable state-transition error (never an exception).
    /// </summary>
    public async Task<Result<RunExecutionOutcome>> ExecuteAsync(RunId runId, CancellationToken ct)
    {
        var loaded = await _runs.GetRunAsync(runId, ct);
        if (!loaded.IsSuccess)
            return Result<RunExecutionOutcome>.Fail(loaded.Error!);
        if (loaded.Value is not { } details)
            return Result<RunExecutionOutcome>.Fail(RunErrors.NotFoundError());

        var run = details.Run;
        if (run.Status is not (RunStatus.Claimed or RunStatus.Running))
            return Result<RunExecutionOutcome>.Fail(
                RunErrors.StateTransitionRejectedError(run.Status, RunStatus.Running));

        WorkflowDefinition workflow;
        RunBudget budget;
        try
        {
            workflow = WorkflowDefinition.FromJson(ParseJson(details.Snapshot.WorkflowSnapshotJson));
            budget = RunBudget.FromJson(ParseJson(details.Snapshot.BudgetSnapshotJson));
        }
        catch (Exception ex) when (ex is JsonException or ArgumentNullException or InvalidOperationException)
        {
            return Result<RunExecutionOutcome>.Fail(RunErrors.SnapshotCanonicalError());
        }

        // Transition Claimed → Running before the pass so the run records its start (state machine
        // enforces legality; MarkRunning is idempotent when already Running).
        var running = run.Status == RunStatus.Claimed ? run.MarkRunning(_clock.UtcNow) : run;

        var variables = BuildVariableStore(running, details.Snapshot);

        InterpretationResult result;
        try
        {
            result = WorkflowInterpreter.Interpret(
                workflow, running, details.Steps, budget, variables,
                _executor, _ids, _clock, ct,
                cancellationRequested: run.CancellationRequestedAtUtc is not null);
        }
        catch (DomainException dex)
        {
            return Result<RunExecutionOutcome>.Fail(dex.Error);
        }

        var persisted = await _runs.PersistExecutionResultAsync(result.Run, result.Steps, result.Events, ct);
        if (!persisted.IsSuccess)
            return Result<RunExecutionOutcome>.Fail(persisted.Error!);

        return Result<RunExecutionOutcome>.Ok(
            new RunExecutionOutcome(result.Run, result.Steps, result.Events, result.Halted));
    }

    /// <summary>
    /// Builds the run-scoped variable store (doc 03 §4). The frozen binding snapshot provides
    /// <c>trigger.*</c>; the run header provides <c>run.*</c>; <c>system.*</c> carries the clock. No
    /// secret is ever placed in the store (VariableStore additionally rejects the <c>secrets</c> root).
    /// </summary>
    private VariableStore BuildVariableStore(AutomationRun run, RunSnapshot snapshot)
    {
        var triggerVars = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        if (TryParseObject(snapshot.BindingSnapshotJson, out var binding))
            foreach (var kv in binding!)
                if (kv.Value is not null)
                    triggerVars[kv.Key] = kv.Value.DeepClone();

        var runVars = new Dictionary<string, JsonNode>(StringComparer.Ordinal)
        {
            ["id"] = JsonValue.Create(run.Id.Value),
            ["priority"] = JsonValue.Create(run.Priority),
            ["trigger_kind"] = JsonValue.Create(run.TriggerKind.ToStorage())
        };

        var systemVars = new Dictionary<string, JsonNode>(StringComparer.Ordinal)
        {
            ["now"] = JsonValue.Create(_clock.UtcNow.ToString("O"))
        };

        return new VariableStore(triggerVars, runVars, systemVars);
    }

    private static JsonNode ParseJson(string json)
        => JsonNode.Parse(json) ?? throw new JsonException("null document");

    private static bool TryParseObject(string? json, out JsonObject? obj)
    {
        obj = null;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            obj = JsonNode.Parse(json) as JsonObject;
            return obj is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
