using System;
using WorkPilot.Contracts.Primitives;

namespace WorkPilot.Domain.Automation.Run.Interpreter;

/// <summary>
/// Pure, monotonic budget tracker (RUN-A13). Tracks four independent dimensions — wall-clock seconds,
/// model turns, capability calls, and result bytes — against the revision's <see cref="RunBudget"/>
/// envelope, seeded with the run's already-consumed counters (so a resumed/re-read run continues from
/// its real position). <see cref="Reserve"/> deducts the proposed cost up-front; on any dimension
/// exceeding its ceiling it returns the corresponding stable <see cref="AppError"/> and leaves all
/// counters unchanged (all-or-nothing). The interpreter stops scheduling further steps once a
/// reservation fails.
/// </summary>
public sealed class BudgetTracker
{
    private long _wallClockSecondsRemaining;
    private int _modelTurnsRemaining;
    private int _capabilityCallsRemaining;
    private long _resultBytesRemaining;

    public BudgetTracker(RunBudget budget, int consumedModelTurns, int consumedCapabilityCalls,
        long consumedResultBytes, int consumedActiveDurationMs)
    {
        _wallClockSecondsRemaining = Math.Max(0, budget.MaxWallClockSeconds - consumedActiveDurationMs / 1000L);
        _modelTurnsRemaining = Math.Max(0, budget.MaxModelTurns - consumedModelTurns);
        _capabilityCallsRemaining = Math.Max(0, budget.MaxCapabilityCalls - consumedCapabilityCalls);
        _resultBytesRemaining = Math.Max(0, budget.MaxResultBytes - consumedResultBytes);
    }

    /// <summary>
    /// Attempts to reserve one step's cost. Returns a failed <see cref="Result{T}"/> carrying the
    /// stable budget-exceeded error (no counter mutation) when any dimension would go negative.
    /// </summary>
    public Result<string?> Reserve(int modelTurns, int capabilityCalls, long resultBytes, long wallClockSeconds, string nodeId)
    {
        if (wallClockSeconds > _wallClockSecondsRemaining)
            return Result<string?>.Fail(RunErrors.RunWallClockBudgetExceededError(nodeId));
        if (modelTurns > _modelTurnsRemaining)
            return Result<string?>.Fail(RunErrors.ModelTurnBudgetExceededError(nodeId));
        if (capabilityCalls > _capabilityCallsRemaining)
            return Result<string?>.Fail(RunErrors.CapabilityCallBudgetExceededError(nodeId));
        if (resultBytes > _resultBytesRemaining)
            return Result<string?>.Fail(RunErrors.ResultBudgetExceededError(nodeId));

        _wallClockSecondsRemaining -= wallClockSeconds;
        _modelTurnsRemaining -= modelTurns;
        _capabilityCallsRemaining -= capabilityCalls;
        _resultBytesRemaining -= resultBytes;
        return Result<string?>.Ok(null);
    }

    public long WallClockSecondsRemaining => _wallClockSecondsRemaining;
    public int ModelTurnsRemaining => _modelTurnsRemaining;
    public int CapabilityCallsRemaining => _capabilityCallsRemaining;
    public long ResultBytesRemaining => _resultBytesRemaining;
}
