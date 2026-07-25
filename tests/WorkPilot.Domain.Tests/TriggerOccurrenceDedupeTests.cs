using System;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation.Run.Materialization;
using Xunit;

namespace WorkPilot.Domain.Tests;

/// <summary>RUN-009/010: the dedupe key must be deterministic and unique per (automation, revision, trigger, instant).</summary>
public class TriggerOccurrenceDedupeTests
{
    private static readonly AutomationId A = AutomationId.Parse("auto_1");
    private static readonly AutomationRevisionId R = AutomationRevisionId.Parse("rev_1");
    private static readonly DateTimeOffset T = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Compute_is_deterministic_and_64_hex_lowercase()
    {
        var k1 = TriggerOccurrenceDedupe.Compute(A, R, "t1", T);
        var k2 = TriggerOccurrenceDedupe.Compute(A, R, "t1", T);
        Assert.Equal(k1, k2);
        Assert.Equal(64, k1.Length);
        Assert.All(k1, c => Assert.True(char.IsDigit(c) || (c >= 'a' && c <= 'f')));
    }

    [Fact]
    public void Compute_differs_by_every_field()
    {
        var baseKey = TriggerOccurrenceDedupe.Compute(A, R, "t1", T);
        Assert.NotEqual(baseKey, TriggerOccurrenceDedupe.Compute(A, R, "t2", T)); // trigger id
        Assert.NotEqual(baseKey, TriggerOccurrenceDedupe.Compute(A, R, "t1", T.AddSeconds(1))); // instant
        Assert.NotEqual(baseKey, TriggerOccurrenceDedupe.Compute(AutomationId.Parse("auto_2"), R, "t1", T)); // automation
        Assert.NotEqual(baseKey, TriggerOccurrenceDedupe.Compute(A, AutomationRevisionId.Parse("rev_2"), "t1", T)); // revision
    }

    [Fact]
    public void Compute_throws_on_empty_trigger_id()
        => Assert.Throws<ArgumentException>(() => TriggerOccurrenceDedupe.Compute(A, R, "  ", T));
}
