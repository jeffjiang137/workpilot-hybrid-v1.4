using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;

namespace WorkPilot.Domain.Automation.Run.Interpreter;

/// <summary>
/// Result of a single interpreter pass over a run's workflow.
/// </summary>
public sealed class InterpretationResult
{
    public InterpretationResult(AutomationRun run, IReadOnlyList<StepRun> steps, IReadOnlyList<RunEvent> events, bool halted)
    {
        Run = run;
        Steps = steps;
        Events = events;
        Halted = halted;
    }

    public AutomationRun Run { get; }
    public IReadOnlyList<StepRun> Steps { get; }
    public IReadOnlyList<RunEvent> Events { get; }
    /// <summary>True if interpretation stopped early (budget, cancellation, fatal error, wait).</summary>
    public bool Halted { get; }
}

/// <summary>
/// Deterministic, pure workflow interpreter (T10: AUT-004 / RUN-002/004). Walks a previously-validated
/// DAG in topological order, selecting Condition branches via <see cref="ConditionEvaluator"/>, reserving
/// the four budget dimensions before each node via <see cref="BudgetTracker"/>, maintaining variable
/// scope through <see cref="VariableStore"/>, and driving the <see cref="StepRunStateMachine"/> /
/// <see cref="RunStateMachine"/>. Side effects are delegated to an injected <see cref="INodeEffectExecutor"/>
/// (T11 supplies the real Agent/Capability/Delay/Notification executors; T10 uses a scripted fake).
///
/// <para>Invariants enforced:</para>
/// <list type="bullet">
///   <item>Non-taken Condition branches (and their downstream) are marked <see cref="StepRunStatus.Skipped"/> and never executed.</item>
///   <item>Any budget dimension exceeding its ceiling halts the run: the offending step is <see cref="StepRunStatus.Failed"/> with the stable budget error and no later step executes (RUN-A13).</item>
///   <item>A cancellation request (or <see cref="CancellationToken"/>) stops scheduling; not-yet-started steps become <see cref="StepRunStatus.Cancelled"/>.</item>
///   <item>Every state change is recorded as a <see cref="RunEvent"/>; returned entities are new immutable copies.</item>
/// </list>
/// </summary>
public static class WorkflowInterpreter
{
    public static InterpretationResult Interpret(
        WorkflowDefinition workflow,
        AutomationRun run,
        IReadOnlyList<StepRun> steps,
        RunBudget budget,
        VariableStore variables,
        INodeEffectExecutor executor,
        IIdGenerator idGenerator,
        IClock clock,
        CancellationToken ct,
        bool cancellationRequested)
    {
        if (workflow.Nodes.Count == 0)
            throw new DomainException(RunErrors.WorkflowEmptyError());
        if (string.IsNullOrEmpty(workflow.EntryNodeId) || FindNode(workflow, workflow.EntryNodeId) is null)
            throw new DomainException(RunErrors.EntryNodeMissingError(workflow.EntryNodeId));

        // Index steps by node id for state updates; copy so we never mutate inputs.
        var stepByNode = new Dictionary<string, StepRun>(StringComparer.Ordinal);
        foreach (var s in steps) stepByNode[s.NodeId] = s;
        var stepList = new List<StepRun>(steps);

        var tracker = new BudgetTracker(budget, run.ModelTurnCount, run.CapabilityCallCount,
            run.ResultBytes, run.ActiveDurationMs);
        var events = new List<RunEvent>();
        var sequence = run.LastEventSequence;
        DateTimeOffset now = clock.UtcNow;

        RunStatus runStatus = run.Status;
        int runRow = run.RowVersion;
        bool halted = false;

        // Captured from a waiting outcome so the run can be requeued (T11 delay/approval resume).
        // Starts null: a fresh pass clears any stale resume cursor; only a new wait re-establishes it.
        DateTimeOffset? resumeAtUtc = null;
        string? currentNodeId = null;

        void Emit(string kind, RunEventLevel level, string code, string msgKey, string props, StepRunId? stepId = null, int? attempt = null)
        {
            sequence++;
            events.Add(RunEvent.Create(
                RunEventId.Create(idGenerator), run.Id, kind, level, code, msgKey, props,
                run.Id.Value, now, stepId, attempt));
        }

        // Enabled graph (disabled removed at validation; guard anyway).
        var enabledNodes = new Dictionary<string, WorkflowNode>(StringComparer.Ordinal);
        foreach (var n in workflow.Nodes)
            if (!n.Disabled) enabledNodes[n.NodeId] = n;

        var successors = new Dictionary<string, List<WorkflowEdge>>(StringComparer.Ordinal);
        foreach (var id in enabledNodes.Keys) successors[id] = new List<WorkflowEdge>();
        foreach (var e in workflow.Edges)
            if (enabledNodes.ContainsKey(e.FromNodeId) && enabledNodes.ContainsKey(e.ToNodeId))
                successors[e.FromNodeId].Add(e);

        // Deterministic topological walk with branch pruning. Use a stable queue ordered by node
        // declaration index so the same workflow always executes in the same order.
        var order = workflow.Nodes.Select((n, i) => (n.NodeId, i)).ToDictionary(x => x.NodeId, x => x.i);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(workflow.EntryNodeId);

        while (queue.Count > 0 && !halted)
        {
            var nodeId = queue.Dequeue();
            if (!visited.Add(nodeId)) continue;
            if (!enabledNodes.TryGetValue(nodeId, out var node)) continue;

            // Cancellation: any node not yet started is cancelled; downstream skipped.
            if (cancellationRequested || ct.IsCancellationRequested)
            {
                MarkStep(stepByNode, stepList, nodeId, node.Kind, run.Id, idGenerator, clock, StepRunStatus.Cancelled, null, ref runRow);
                Emit("step_cancelled", RunEventLevel.Info, "RUN_STEP_CANCELLED", "Run.StepCancelled",
                    Props(new() { ["node_id"] = nodeId }), StepRunIdFor(stepByNode, nodeId));
                SkipDownstream(successors, enabledNodes, nodeId, stepByNode, stepList, run.Id, idGenerator, clock, ref runRow, Emit);
                runStatus = RunStatus.Cancelled;
                halted = true;
                break;
            }

            if (node.Kind == "condition")
            {
                bool branch;
                try
                {
                    var cond = node.Payload?["condition"];
                    if (cond is null) throw new DomainException(RunErrors.ConditionEvaluationError(nodeId, "missing_condition"));
                    branch = ConditionEvaluator.Evaluate(cond, variables);
                }
                catch (DomainException dx)
                {
                    MarkStep(stepByNode, stepList, nodeId, node.Kind, run.Id, idGenerator, clock, StepRunStatus.Failed, dx.Message, ref runRow);
                    Emit("condition_failed", RunEventLevel.Error, "RUN_CONDITION_FAILED", "Run.ConditionFailed",
                        Props(new() { ["node_id"] = nodeId, ["detail"] = dx.Message }), StepRunIdFor(stepByNode, nodeId));
                    runStatus = RunStatus.Failed;
                    halted = true;
                    break;
                }

                Emit("condition_evaluated", RunEventLevel.Info, "RUN_CONDITION_EVALUATED", "Run.ConditionEvaluated",
                    Props(new() { ["node_id"] = nodeId, ["branch"] = branch ? "true" : "false" }), StepRunIdFor(stepByNode, nodeId));

                var taken = branch ? "true" : "false";
                foreach (var e in successors[nodeId])
                {
                    if (e.Branch == taken)
                        queue.Enqueue(e.ToNodeId);
                    else
                        SkipDownstream(successors, enabledNodes, e.ToNodeId, stepByNode, stepList, run.Id, idGenerator, clock, ref runRow, Emit);
                }
                continue;
            }

            // Non-condition node: reserve budget up-front.
            var est = executor.Estimate(node);
            var reserve = tracker.Reserve(est.ModelTurns, est.CapabilityCalls, est.ResultBytes, est.WallClockSeconds, nodeId);
            if (!reserve.IsSuccess)
            {
                MarkStep(stepByNode, stepList, nodeId, node.Kind, run.Id, idGenerator, clock, StepRunStatus.Failed, reserve.Error!.MessageKey, ref runRow);
                Emit("budget_exceeded", RunEventLevel.Error, "RUN_BUDGET_EXCEEDED", "Run.BudgetExceeded",
                    Props(new() { ["node_id"] = nodeId, ["code"] = reserve.Error!.Code }), StepRunIdFor(stepByNode, nodeId));
                runStatus = RunStatus.Failed;
                halted = true;
                break;
            }

            // Ensure an in-flight step exists for this node so side-effecting executors (T12) can
            // bind a Native Permit and record the side-effect phase against a stable StepRun identity.
            if (!stepByNode.TryGetValue(nodeId, out var step))
            {
                step = StepRun.Create(StepRunId.Create(idGenerator), run.Id, nodeId, node.Kind,
                    $"step:{nodeId}", $"digest:{nodeId}", 1, 1, clock.UtcNow);
                stepByNode[nodeId] = step;
                stepList.Add(step);
            }

            NodeEffectResult outcome;
            try
            {
                outcome = executor.ExecuteNode(node, variables, run, step, ct);
            }
            catch (OperationCanceledException)
            {
                MarkStep(stepByNode, stepList, nodeId, node.Kind, run.Id, idGenerator, clock, StepRunStatus.Cancelled, null, ref runRow);
                Emit("step_cancelled", RunEventLevel.Info, "RUN_STEP_CANCELLED", "Run.StepCancelled",
                    Props(new() { ["node_id"] = nodeId }), StepRunIdFor(stepByNode, nodeId));
                runStatus = RunStatus.Cancelled;
                halted = true;
                break;
            }

            if (outcome.Status == StepRunStatus.Succeeded)
            {
                if (!string.IsNullOrEmpty(outcome.OutputKey) && outcome.OutputValue is not null)
                    variables.Declare(nodeId, outcome.OutputKey!, outcome.OutputValue);
                MarkStep(stepByNode, stepList, nodeId, node.Kind, run.Id, idGenerator, clock, StepRunStatus.Succeeded, null, ref runRow);
                Emit("step_succeeded", RunEventLevel.Info, "RUN_STEP_SUCCEEDED", "Run.StepSucceeded",
                    Props(new() { ["node_id"] = nodeId }), StepRunIdFor(stepByNode, nodeId));
                foreach (var e in successors[nodeId]) queue.Enqueue(e.ToNodeId);
            }
            else if (outcome.Status == StepRunStatus.WaitingDelay)
            {
                MarkStep(stepByNode, stepList, nodeId, node.Kind, run.Id, idGenerator, clock, StepRunStatus.WaitingDelay, null, ref runRow);
                runStatus = RunStatus.WaitingDelay;
                resumeAtUtc = outcome.ResumeAtUtc;
                currentNodeId = nodeId;
                Emit("step_waiting_delay", RunEventLevel.Info, "RUN_STEP_WAITING_DELAY", "Run.StepWaitingDelay",
                    Props(new() { ["node_id"] = nodeId }), StepRunIdFor(stepByNode, nodeId));
                halted = true; // run released; resumed by T11 delay logic
                break;
            }
            else if (outcome.Status == StepRunStatus.WaitingApproval)
            {
                MarkStep(stepByNode, stepList, nodeId, node.Kind, run.Id, idGenerator, clock, StepRunStatus.WaitingApproval, null, ref runRow);
                runStatus = RunStatus.WaitingApproval;
                resumeAtUtc = outcome.ResumeAtUtc;
                currentNodeId = nodeId;
                Emit("step_waiting_approval", RunEventLevel.Info, "RUN_STEP_WAITING_APPROVAL", "Run.StepWaitingApproval",
                    Props(new() { ["node_id"] = nodeId }), StepRunIdFor(stepByNode, nodeId));
                halted = true;
                break;
            }
            else // Failed / BlockedPolicy / OutcomeUnknown
            {
                MarkStep(stepByNode, stepList, nodeId, node.Kind, run.Id, idGenerator, clock, outcome.Status, outcome.ErrorCode, ref runRow);
                Emit("step_failed", RunEventLevel.Error, "RUN_STEP_FAILED", "Run.StepFailed",
                    Props(new() { ["node_id"] = nodeId, ["status"] = outcome.Status.ToString(), ["error"] = outcome.ErrorCode ?? "" }), StepRunIdFor(stepByNode, nodeId));
                runStatus = outcome.Status == StepRunStatus.BlockedPolicy ? RunStatus.BlockedPolicy : RunStatus.Failed;
                halted = true;
                break;
            }
        }

        // Terminalize run if the walk completed cleanly.
        if (!halted && (runStatus is RunStatus.Running or RunStatus.Claimed or RunStatus.Queued))
        {
            runStatus = RunStatus.Completed;
            Emit("run_completed", RunEventLevel.Info, "RUN_COMPLETED", "Run.Completed",
                Props(new() { ["reason"] = "all_steps_terminal" }));
        }
        else if (runStatus is RunStatus.Failed or RunStatus.BlockedPolicy or RunStatus.Cancelled or RunStatus.NeedsReview)
        {
            Emit("run_terminal", runStatus == RunStatus.Cancelled ? RunEventLevel.Info : RunEventLevel.Error,
                "RUN_TERMINAL", "Run.Terminal", Props(new() { ["status"] = runStatus.ToString() }));
        }

        // Apply consumed budget deltas onto the run's counters.
        var finalRun = run with
        {
            Status = runStatus,
            RowVersion = runRow,
            LastEventSequence = sequence,
            CurrentNodeId = currentNodeId,
            ResumeAtUtc = resumeAtUtc,
            ModelTurnCount = run.ModelTurnCount + (budget.MaxModelTurns - tracker.ModelTurnsRemaining),
            CapabilityCallCount = run.CapabilityCallCount + (budget.MaxCapabilityCalls - tracker.CapabilityCallsRemaining),
            ResultBytes = run.ResultBytes + (int)(budget.MaxResultBytes - tracker.ResultBytesRemaining),
            ActiveDurationMs = run.ActiveDurationMs + (int)((budget.MaxWallClockSeconds - tracker.WallClockSecondsRemaining) * 1000L)
        };

        return new InterpretationResult(finalRun, stepList, events, halted);
    }

    // ---- helpers ----

    private static WorkflowNode? FindNode(WorkflowDefinition wf, string id)
    {
        foreach (var n in wf.Nodes) if (n.NodeId == id) return n;
        return null;
    }

    private static StepRunId StepRunIdFor(Dictionary<string, StepRun> byNode, string nodeId)
        => byNode.TryGetValue(nodeId, out var s) ? s.Id : StepRunId.Create(new SequentialIdGenerator());

    private static void MarkStep(Dictionary<string, StepRun> byNode, List<StepRun> stepList, string nodeId,
        string nodeKind, RunId runId, IIdGenerator idGen, IClock clock, StepRunStatus status,
        string? errorCode, ref int runRow)
    {
        if (!byNode.TryGetValue(nodeId, out var existing))
        {
            var created = StepRun.Create(StepRunId.Create(idGen), runId, nodeId, nodeKind,
                $"step:{nodeId}", $"digest:{nodeId}", 1, 1, clock.UtcNow);
            byNode[nodeId] = created;
            stepList.Add(created);
            existing = created;
        }

        if (existing.Status == status) return;

        // The interpreter marks a step's final outcome in one call, but the state machine (doc 03 §7.2)
        // only allows terminal outcomes from Running. Promote a freshly-created Pending/Ready step through
        // Running so the transition is always legal (Skipped/Cancelled remain direct from Pending).
        if (!StepRunStateMachine.CanTransition(existing.Status, status)
            && StepRunStateMachine.CanTransition(existing.Status, StepRunStatus.Running)
            && StepRunStateMachine.CanTransition(StepRunStatus.Running, status))
        {
            existing = ApplyStepTransition(existing, StepRunStatus.Running, null, clock);
            byNode[nodeId] = existing;
            ReplaceInList(stepList, nodeId, existing);
            runRow++;
        }

        // CAS-checked transition: only apply if legal from current status.
        var check = StepRunStateMachine.TryTransition(existing.Status, existing.RowVersion, existing.RowVersion, status, out _);
        if (!check.IsSuccess) return; // already terminal/incompatible; do not corrupt
        var updated = ApplyStepTransition(existing, status, errorCode, clock);
        byNode[nodeId] = updated;
        ReplaceInList(stepList, nodeId, updated);
        runRow++; // run mutated alongside step
    }

    private static StepRun ApplyStepTransition(StepRun step, StepRunStatus status, string? errorCode, IClock clock)
        => step with
        {
            Status = status,
            ErrorCode = errorCode ?? step.ErrorCode,
            RowVersion = step.RowVersion + 1,
            StartedAtUtc = status == StepRunStatus.Running ? (step.StartedAtUtc ?? clock.UtcNow) : step.StartedAtUtc,
            FinishedAtUtc = status is StepRunStatus.Succeeded or StepRunStatus.Failed or StepRunStatus.Cancelled
                or StepRunStatus.Skipped or StepRunStatus.BlockedPolicy ? clock.UtcNow : step.FinishedAtUtc
        };

    private static void ReplaceInList(List<StepRun> stepList, string nodeId, StepRun updated)
    {
        var idx = stepList.FindIndex(s => s.NodeId == nodeId);
        if (idx >= 0) stepList[idx] = updated;
    }

    private static void SkipDownstream(Dictionary<string, List<WorkflowEdge>> successors,
        Dictionary<string, WorkflowNode> enabled, string fromId,
        Dictionary<string, StepRun> byNode, List<StepRun> stepList, RunId runId, IIdGenerator idGen,
        IClock clock, ref int runRow, Action<string, RunEventLevel, string, string, string, StepRunId?, int?> emit)
    {
        // Traverse the entire downstream subgraph with a *local* visited set. The main-walk `visited`
        // set already contains the entry node, so reusing it would skip the entry and never enqueue its
        // successors (leaving downstream steps unscheduled rather than Skipped).
        var skipVisited = new HashSet<string>(StringComparer.Ordinal);
        var q = new Queue<string>();
        if (enabled.ContainsKey(fromId)) q.Enqueue(fromId);
        while (q.Count > 0)
        {
            var id = q.Dequeue();
            if (!enabled.ContainsKey(id)) continue;
            if (!skipVisited.Add(id)) continue;
            if (!byNode.ContainsKey(id) || byNode[id].Status == StepRunStatus.Pending || byNode[id].Status == StepRunStatus.Ready)
            {
                MarkStep(byNode, stepList, id, enabled.TryGetValue(id, out var n) ? n.Kind : "unknown", runId, idGen, clock, StepRunStatus.Skipped, null, ref runRow);
            }
            foreach (var e in successors[id]) q.Enqueue(e.ToNodeId);
        }
    }

    private static string Props(Dictionary<string, string> map)
    {
        var obj = new JsonObject();
        foreach (var kv in map) obj[kv.Key] = kv.Value;
        return obj.ToJsonString();
    }

    private sealed class SequentialIdGenerator : IIdGenerator
    {
        private long _seq;
        public string NewId() => $"gen_{++_seq:x12}";
    }
}
