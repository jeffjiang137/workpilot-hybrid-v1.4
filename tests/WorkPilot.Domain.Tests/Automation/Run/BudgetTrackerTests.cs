using WorkPilot.Domain.Automation;
using WorkPilot.Domain.Automation.Run.Interpreter;
using Xunit;

namespace WorkPilot.Domain.Tests.Automation.Run;

/// <summary>Four-dimension budget reservation (RUN-A13). Each dimension has an independent ceiling and
/// all-or-nothing reservation semantics.</summary>
public class BudgetTrackerTests
{
    private static RunBudget Budget => new(MaxModelTurns: 3, MaxTotalTokens: 100_000,
        MaxWallClockSeconds: 300, MaxCapabilityCalls: 2, MaxResultBytes: 10_000);

    [Fact]
    public void Reserve_within_budget_succeeds_and_decrements()
    {
        var t = new BudgetTracker(Budget, 0, 0, 0, 0);
        var r = t.Reserve(modelTurns: 1, capabilityCalls: 1, resultBytes: 1000, wallClockSeconds: 10, "n1");
        Assert.True(r.IsSuccess);
        Assert.Equal(2, t.ModelTurnsRemaining);
        Assert.Equal(1, t.CapabilityCallsRemaining);
        Assert.Equal(9000, t.ResultBytesRemaining);
        Assert.Equal(290, t.WallClockSecondsRemaining);
    }

    [Fact]
    public void Model_turn_exhaustion_yields_stable_error()
    {
        var t = new BudgetTracker(Budget, 0, 0, 0, 0);
        var r = t.Reserve(4, 0, 0, 0, "n1");
        Assert.False(r.IsSuccess);
        Assert.Equal("RUN_BUDGET_MODEL_TURN", r.Error!.Code);
        Assert.Equal(3, t.ModelTurnsRemaining); // unchanged
    }

    [Fact]
    public void Capability_call_exhaustion_yields_stable_error()
    {
        var t = new BudgetTracker(Budget, 0, 0, 0, 0);
        var r = t.Reserve(0, 3, 0, 0, "n1");
        Assert.False(r.IsSuccess);
        Assert.Equal("RUN_BUDGET_CAPABILITY", r.Error!.Code);
    }

    [Fact]
    public void Result_byte_exhaustion_yields_stable_error()
    {
        var t = new BudgetTracker(Budget, 0, 0, 0, 0);
        var r = t.Reserve(0, 0, 10_001, 0, "n1");
        Assert.False(r.IsSuccess);
        Assert.Equal("RUN_BUDGET_RESULT", r.Error!.Code);
    }

    [Fact]
    public void Wall_clock_exhaustion_yields_stable_error()
    {
        var t = new BudgetTracker(Budget, 0, 0, 0, 0);
        var r = t.Reserve(0, 0, 0, 301, "n1");
        Assert.False(r.IsSuccess);
        Assert.Equal("RUN_BUDGET_WALLCLOCK", r.Error!.Code);
    }

    [Fact]
    public void Seeded_from_already_consumed_counters()
    {
        // Two model turns already consumed => only one remains.
        var t = new BudgetTracker(Budget, consumedModelTurns: 2, consumedCapabilityCalls: 0,
            consumedResultBytes: 0, consumedActiveDurationMs: 0);
        Assert.Equal(1, t.ModelTurnsRemaining);
        Assert.True(t.Reserve(1, 0, 0, 0, "n1").IsSuccess);
        Assert.False(t.Reserve(1, 0, 0, 0, "n2").IsSuccess);
    }

    [Fact]
    public void Reservation_is_all_or_nothing_across_dimensions()
    {
        var t = new BudgetTracker(Budget, 0, 0, 0, 0);
        // model ok but result bytes exceed => nothing deducted.
        var r = t.Reserve(1, 0, 10_001, 0, "n1");
        Assert.False(r.IsSuccess);
        Assert.Equal(3, t.ModelTurnsRemaining);
        Assert.Equal(10_000, t.ResultBytesRemaining);
    }
}
