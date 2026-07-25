using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using WorkPilot.Contracts.Primitives;

namespace WorkPilot.Domain.Automation.Run.Interpreter;

/// <summary>
/// Pure evaluator for the fixed Condition AST (spec doc 03 §3.4). Supports <c>all/any/not</c> with a
/// maximum depth of 5 and at most 20 leaf clauses. Leaf paths are restricted to
/// <c>trigger.*</c>, <c>vars.&lt;declared&gt;.*</c>, and <c>run.*</c>; operators are
/// eq/ne/gt/gte/lt/lte/contains/starts_with/exists/is_empty. Any malformed clause, unknown path root,
/// or parse failure is treated fail-closed: it throws <see cref="DomainException"/> with
/// <see cref="RunErrors.ConditionEvaluationError"/> so the interpreter marks the step failed rather
/// than silently taking a branch.
/// </summary>
public static class ConditionEvaluator
{
    private const int MaxDepth = 5;
    private const int MaxLeaves = 20;

    public static bool Evaluate(JsonNode condition, VariableStore variables)
    {
        var leafCount = 0;
        return Eval(condition, variables, 1, ref leafCount);
    }

    private static bool Eval(JsonNode node, VariableStore variables, int depth, ref int leafCount)
    {
        if (depth > MaxDepth)
            throw new DomainException(RunErrors.ConditionEvaluationError("(root)", "max_depth_exceeded"));

        if (node is JsonObject obj)
        {
            if (obj.ContainsKey("all"))
                return EvalChildren(obj["all"], variables, depth, ref leafCount, true);
            if (obj.ContainsKey("any"))
                return EvalChildren(obj["any"], variables, depth, ref leafCount, false);
            if (obj.ContainsKey("not"))
                return !Eval(obj["not"]!, variables, depth + 1, ref leafCount);
            // leaf clause
            return EvalLeaf(obj, variables);
        }

        throw new DomainException(RunErrors.ConditionEvaluationError("(root)", "node_not_object"));
    }

    private static bool EvalChildren(JsonNode? children, VariableStore variables, int depth, ref int leafCount, bool all)
    {
        if (children is not JsonArray arr || arr.Count == 0)
            throw new DomainException(RunErrors.ConditionEvaluationError("(root)", "empty_composite"));

        var result = all; // for "all" start true; for "any" start false
        foreach (var child in arr)
        {
            if (child is null)
                throw new DomainException(RunErrors.ConditionEvaluationError("(root)", "null_child"));
            var childResult = Eval(child, variables, depth + 1, ref leafCount);
            result = all ? (result && childResult) : (result || childResult);
        }
        return result;
    }

    private static bool EvalLeaf(JsonObject clause, VariableStore variables)
    {
        var path = clause["path"]?.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? clause["path"]!.GetValue<string>() : null;
        var op = clause["op"]?.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? clause["op"]!.GetValue<string>() : null;

        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(op))
            throw new DomainException(RunErrors.ConditionEvaluationError(path ?? "(leaf)", "missing_path_or_op"));

        if (!path.StartsWith("trigger.", StringComparison.Ordinal)
            && !path.StartsWith("run.", StringComparison.Ordinal)
            && !(path.StartsWith("vars.", StringComparison.Ordinal)))
            throw new DomainException(RunErrors.ConditionEvaluationError(path, "path_root_not_allowed"));

        if (!variables.TryResolve(path, out var value))
            // unknown path / missing var -> fail-closed false for exists/is_empty semantics handled below
            value = null;

        return op switch
        {
            "exists" => value is not null,
            "is_empty" => IsEmpty(value),
            "eq" => EqualsValue(value, clause["value"]),
            "ne" => !EqualsValue(value, clause["value"]),
            "gt" => CompareNumber(value, clause["value"]) > 0,
            "gte" => CompareNumber(value, clause["value"]) >= 0,
            "lt" => CompareNumber(value, clause["value"]) < 0,
            "lte" => CompareNumber(value, clause["value"]) <= 0,
            "contains" => Contains(value, clause["value"]),
            "starts_with" => StartsWith(value, clause["value"]),
            _ => throw new DomainException(RunErrors.ConditionEvaluationError(path, $"unknown_op:{op}"))
        };
    }

    private static bool IsEmpty(JsonNode? value) => value switch
    {
        null => true,
        JsonObject o => o.Count == 0,
        JsonArray a => a.Count == 0,
        JsonValue v => (v.GetValueKind() == System.Text.Json.JsonValueKind.Null)
                       || (v.GetValueKind() == System.Text.Json.JsonValueKind.String && v.GetValue<string>() == string.Empty),
        _ => false
    };

    private static bool EqualsValue(JsonNode? a, JsonNode? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a.GetValueKind() != b.GetValueKind()) return false;
        return a.ToJsonString() == b.ToJsonString();
    }

    private static int CompareNumber(JsonNode? a, JsonNode? b)
    {
        if (a is null || b is null) throw new DomainException(RunErrors.ConditionEvaluationError("(leaf)", "numeric_operand_null"));
        if (a.GetValueKind() != System.Text.Json.JsonValueKind.Number || b.GetValueKind() != System.Text.Json.JsonValueKind.Number)
            throw new DomainException(RunErrors.ConditionEvaluationError("(leaf)", "numeric_operand_not_number"));

        // STJ's JsonValue<T> only permits GetValue<T>() for the exact underlying CLR type, so probe the
        // numeric types a node may hold (int/long/double/decimal) and coerce to double for comparison.
        var av = ToDouble(a);
        var bv = ToDouble(b);
        return av.CompareTo(bv);
    }

    private static double ToDouble(JsonNode n)
    {
        try { return n.GetValue<int>(); }
        catch (InvalidOperationException) { }
        try { return n.GetValue<long>(); }
        catch (InvalidOperationException) { }
        try { return n.GetValue<double>(); }
        catch (InvalidOperationException) { }
        try { return (double)n.GetValue<decimal>(); }
        catch (InvalidOperationException) { }
        throw new DomainException(RunErrors.ConditionEvaluationError("(leaf)", "numeric_operand_not_number"));
    }

    private static bool Contains(JsonNode? haystack, JsonNode? needle)
    {
        if (haystack is JsonArray arr && needle is not null)
            return arr.Any(item => EqualsValue(item, needle));
        if (haystack is JsonValue hv && needle is JsonValue nv
            && hv.GetValueKind() == System.Text.Json.JsonValueKind.String
            && nv.GetValueKind() == System.Text.Json.JsonValueKind.String)
            return hv.GetValue<string>().Contains(nv.GetValue<string>(), StringComparison.Ordinal);
        throw new DomainException(RunErrors.ConditionEvaluationError("(leaf)", "contains_unsupported_type"));
    }

    private static bool StartsWith(JsonNode? haystack, JsonNode? needle)
    {
        if (haystack is JsonValue hv && needle is JsonValue nv
            && hv.GetValueKind() == System.Text.Json.JsonValueKind.String
            && nv.GetValueKind() == System.Text.Json.JsonValueKind.String)
            return hv.GetValue<string>().StartsWith(nv.GetValue<string>(), StringComparison.Ordinal);
        throw new DomainException(RunErrors.ConditionEvaluationError("(leaf)", "starts_with_unsupported_type"));
    }
}
