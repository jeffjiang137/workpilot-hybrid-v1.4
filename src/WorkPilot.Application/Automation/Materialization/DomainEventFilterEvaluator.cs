using System.Text.Json.Nodes;

namespace WorkPilot.Application.Automation.Materialization;

/// <summary>
/// Pure filter evaluation for domain-event triggers (spec doc 04 §4). A trigger's <c>filters</c> is a
/// JSON array of match clauses; an event payload matches when every clause passes. A clause is an
/// object with a <c>field</c> (top-level key in the payload) and either <c>equals</c> (string) or
/// <c>in</c> (array of strings). Empty/absent filters always match. Side-effect free and unit-tested.
/// </summary>
public static class DomainEventFilterEvaluator
{
    public static bool Matches(System.Text.Json.Nodes.JsonArray? filters, JsonObject? payload)
    {
        if (filters is null || filters.Count == 0)
            return true;
        if (payload is null)
            return false;

        foreach (var clauseNode in filters)
        {
            if (clauseNode is not JsonObject clause)
                return false;
            var field = clause["field"]?.GetValue<string>();
            if (string.IsNullOrEmpty(field) || !payload.TryGetPropertyValue(field!, out var actualNode))
                return false;
            var actual = actualNode?.GetValue<string>();

            if (clause["equals"] is { } equalsNode)
            {
                var expected = equalsNode.GetValue<string>();
                if (!string.Equals(actual, expected, System.StringComparison.Ordinal))
                    return false;
            }
            else if (clause["in"] is JsonArray inArray)
            {
                var matched = false;
                foreach (var item in inArray)
                {
                    if (string.Equals(actual, item?.GetValue<string>(), System.StringComparison.Ordinal))
                    {
                        matched = true;
                        break;
                    }
                }
                if (!matched) return false;
            }
            else
            {
                // A clause with neither equals nor in is malformed -> treat as non-match (fail closed).
                return false;
            }
        }

        return true;
    }
}
