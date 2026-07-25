using System.Collections.Generic;
using System.Text.Json.Nodes;
using WorkPilot.Domain.Automation;
using WorkPilot.Domain.Automation.Run;
using WorkPilot.Domain.Automation.Run.Interpreter;
using Xunit;

namespace WorkPilot.Domain.Tests.Automation.Run;

/// <summary>Condition AST evaluation (doc 03 §3.4): operators, composites, and fail-closed behaviour.</summary>
public class ConditionEvaluatorTests
{
    private static VariableStore Store() => new(
        triggerVars: new Dictionary<string, JsonNode>
        {
            ["count"] = 7,
            ["name"] = "workpilot",
            ["tags"] = new JsonArray { "a", "b", "c" },
            ["empty_str"] = "",
            ["obj"] = new JsonObject { ["k"] = 1 }
        });

    private static JsonObject Leaf(string path, string op, JsonNode? value = null)
    {
        var o = new JsonObject { ["path"] = path, ["op"] = op };
        if (value is not null) o["value"] = value;
        return o;
    }

    [Fact] public void Eq_true() => Assert.True(ConditionEvaluator.Evaluate(Leaf("trigger.count", "eq", 7), Store()));
    [Fact] public void Eq_false() => Assert.False(ConditionEvaluator.Evaluate(Leaf("trigger.count", "eq", 8), Store()));
    [Fact] public void Ne() => Assert.True(ConditionEvaluator.Evaluate(Leaf("trigger.count", "ne", 8), Store()));
    [Fact] public void Gt() => Assert.True(ConditionEvaluator.Evaluate(Leaf("trigger.count", "gt", 5), Store()));
    [Fact] public void Gte_equal() => Assert.True(ConditionEvaluator.Evaluate(Leaf("trigger.count", "gte", 7), Store()));
    [Fact] public void Lt() => Assert.False(ConditionEvaluator.Evaluate(Leaf("trigger.count", "lt", 5), Store()));
    [Fact] public void Lte() => Assert.True(ConditionEvaluator.Evaluate(Leaf("trigger.count", "lte", 7), Store()));
    [Fact] public void Contains_array() => Assert.True(ConditionEvaluator.Evaluate(Leaf("trigger.tags", "contains", "b"), Store()));
    [Fact] public void Contains_string() => Assert.True(ConditionEvaluator.Evaluate(Leaf("trigger.name", "contains", "pilot"), Store()));
    [Fact] public void StartsWith() => Assert.True(ConditionEvaluator.Evaluate(Leaf("trigger.name", "starts_with", "work"), Store()));
    [Fact] public void Exists_true() => Assert.True(ConditionEvaluator.Evaluate(Leaf("trigger.count", "exists"), Store()));
    [Fact] public void Exists_false_for_missing() => Assert.False(ConditionEvaluator.Evaluate(Leaf("trigger.missing", "exists"), Store()));
    [Fact] public void IsEmpty_empty_string() => Assert.True(ConditionEvaluator.Evaluate(Leaf("trigger.empty_str", "is_empty"), Store()));
    [Fact] public void IsEmpty_missing_is_true() => Assert.True(ConditionEvaluator.Evaluate(Leaf("trigger.missing", "is_empty"), Store()));

    [Fact]
    public void All_composite_requires_every_child()
    {
        var cond = new JsonObject
        {
            ["all"] = new JsonArray { Leaf("trigger.count", "gt", 5), Leaf("trigger.name", "eq", "workpilot") }
        };
        Assert.True(ConditionEvaluator.Evaluate(cond, Store()));

        var cond2 = new JsonObject
        {
            ["all"] = new JsonArray { Leaf("trigger.count", "gt", 5), Leaf("trigger.name", "eq", "nope") }
        };
        Assert.False(ConditionEvaluator.Evaluate(cond2, Store()));
    }

    [Fact]
    public void Any_composite_requires_one_child()
    {
        var cond = new JsonObject
        {
            ["any"] = new JsonArray { Leaf("trigger.count", "lt", 0), Leaf("trigger.name", "eq", "workpilot") }
        };
        Assert.True(ConditionEvaluator.Evaluate(cond, Store()));
    }

    [Fact]
    public void Not_negates()
    {
        var cond = new JsonObject { ["not"] = Leaf("trigger.count", "eq", 1) };
        Assert.True(ConditionEvaluator.Evaluate(cond, Store()));
    }

    [Fact]
    public void Unknown_operator_fails_closed()
    {
        var ex = Assert.Throws<DomainException>(() =>
            ConditionEvaluator.Evaluate(Leaf("trigger.count", "between", 3), Store()));
        Assert.Equal("RUN_CONDITION_EVAL", ex.Error.Code);
    }

    [Fact]
    public void Path_root_not_allowed_fails_closed()
    {
        Assert.Throws<DomainException>(() =>
            ConditionEvaluator.Evaluate(Leaf("secrets.token", "exists"), Store()));
        Assert.Throws<DomainException>(() =>
            ConditionEvaluator.Evaluate(Leaf("system.now", "exists"), Store()));
    }

    [Fact]
    public void Missing_path_or_op_fails_closed()
    {
        Assert.Throws<DomainException>(() =>
            ConditionEvaluator.Evaluate(new JsonObject { ["op"] = "eq", ["value"] = 1 }, Store()));
    }

    [Fact]
    public void Empty_composite_fails_closed()
    {
        Assert.Throws<DomainException>(() =>
            ConditionEvaluator.Evaluate(new JsonObject { ["all"] = new JsonArray() }, Store()));
    }

    [Fact]
    public void Numeric_compare_on_non_number_fails_closed()
    {
        Assert.Throws<DomainException>(() =>
            ConditionEvaluator.Evaluate(Leaf("trigger.name", "gt", 3), Store()));
    }

    [Fact]
    public void Exceeding_max_depth_fails_closed()
    {
        // Nest not/not/not... beyond depth 5.
        JsonNode node = Leaf("trigger.count", "eq", 7);
        for (var i = 0; i < 6; i++)
            node = new JsonObject { ["not"] = node };
        Assert.Throws<DomainException>(() => ConditionEvaluator.Evaluate(node, Store()));
    }
}
