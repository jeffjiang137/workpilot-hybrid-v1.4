using System;
using System.Collections.Generic;
using System.Linq;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation.Run.Materialization;
using Xunit;

namespace WorkPilot.Domain.Tests;

/// <summary>RUN-002: the pure claim planner must order by priority, then scheduled, then id, and respect the slot / per-automation caps.</summary>
public class RunQueueSelectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static QueuedRunInfo Q(string runId, string autoId, int priority, DateTimeOffset scheduled)
        => new(RunId.Parse(runId), AutomationId.Parse(autoId), priority, scheduled, Now);

    [Fact]
    public void Select_orders_priority_desc_then_scheduled_asc_then_id_asc()
    {
        // Distinct automations so the per-automation concurrency cap (1) does not suppress any run.
        var list = new List<QueuedRunInfo>
        {
            Q("r3", "a3", 0, Now.AddMinutes(5)),
            Q("r1", "a1", 5, Now.AddMinutes(1)),
            Q("r2", "a2", 5, Now.AddMinutes(2)),
            Q("r4", "a4", 0, Now.AddMinutes(1)),
        };
        var chosen = RunQueueSelector.Select(list, 10).Select(x => x.Value).ToList();
        Assert.Equal(new[] { "r1", "r2", "r4", "r3" }, chosen);
    }

    [Fact]
    public void Select_respects_global_slot_cap()
    {
        // Distinct automations so only the global slot cap bounds the selection.
        var list = Enumerable.Range(0, 10).Select(i => Q($"r{i}", $"a{i}", 0, Now.AddMinutes(i))).ToList();
        var chosen = RunQueueSelector.Select(list, 2);
        Assert.Equal(2, chosen.Count);
        Assert.Equal(new[] { "r0", "r1" }, chosen.Select(x => x.Value));
    }

    [Fact]
    public void Select_enforces_per_automation_concurrency_one()
    {
        // Two queued runs for the SAME automation: even with many global slots only 1 is chosen.
        var list = new List<QueuedRunInfo>
        {
            Q("r1", "auto_x", 5, Now),
            Q("r2", "auto_x", 4, Now.AddMinutes(1)),
            Q("r3", "auto_y", 3, Now.AddMinutes(2)),
        };
        var chosen = RunQueueSelector.Select(list, 10);
        Assert.Equal(2, chosen.Count); // r1 (auto_x) + r3 (auto_y)
        Assert.Contains(RunId.Parse("r1"), chosen);
        Assert.Contains(RunId.Parse("r3"), chosen);
        Assert.DoesNotContain(RunId.Parse("r2"), chosen); // blocked by per-automation concurrency = 1
    }

    [Fact]
    public void Select_returns_empty_when_no_slots()
    {
        var list = new List<QueuedRunInfo> { Q("r1", "a", 0, Now) };
        Assert.Empty(RunQueueSelector.Select(list, 0));
    }

    [Fact]
    public void Slot_bounds_are_well_defined()
    {
        Assert.Equal(2, RunQueueSelector.DefaultGlobalSlots);
        Assert.Equal(4, RunQueueSelector.MaxGlobalSlots);
        Assert.Equal(1, RunQueueSelector.PerAutomationConcurrency);
    }
}
