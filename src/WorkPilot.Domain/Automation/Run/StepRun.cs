using System;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;

namespace WorkPilot.Domain.Automation.Run;

/// <summary>
/// A single step execution within a run (one node, one logical execution, one attempt). The
/// unique key is (run_id, node_id, logical_execution, attempt). Construct via <see cref="Create"/>.
/// </summary>
public sealed record StepRun(
    StepRunId Id,
    RunId RunId,
    string NodeId,
    int LogicalExecution,
    int Attempt,
    string NodeKind,
    StepRunStatus Status,
    SideEffectPhase? SideEffectPhase,
    string IdempotencyKey,
    string InputDigest,
    string? OutputSummaryJson,
    DateTimeOffset? ResumeAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    int DurationMs,
    string? ErrorCode,
    int RowVersion)
{
    public static StepRun Create(
        StepRunId id,
        RunId runId,
        string nodeId,
        string nodeKind,
        string idempotencyKey,
        string inputDigest,
        int logicalExecution = 1,
        int attempt = 1,
        DateTimeOffset? startedAtUtc = null,
        DateTimeOffset? finishedAtUtc = null,
        int durationMs = 0,
        string? outputSummaryJson = null,
        string? errorCode = null,
        int rowVersion = 1)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
            throw new DomainException(RunErrors.StepNodeIdEmptyError());
        if (string.IsNullOrWhiteSpace(nodeKind))
            throw new DomainException(RunErrors.StepNodeKindEmptyError());
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new DomainException(RunErrors.StepIdempotencyEmptyError());
        if (string.IsNullOrWhiteSpace(inputDigest))
            throw new DomainException(RunErrors.StepInputDigestEmptyError());
        if (logicalExecution < 1)
            throw new DomainException(RunErrors.StepLogicalExecutionError());
        if (attempt < 1)
            throw new DomainException(RunErrors.StepAttemptError());

        return new StepRun(id, runId, nodeId, logicalExecution, attempt, nodeKind, StepRunStatus.Pending,
            null, idempotencyKey, inputDigest, outputSummaryJson, null, startedAtUtc, finishedAtUtc,
            durationMs, errorCode, rowVersion);
    }
}
