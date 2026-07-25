using System.Text.Json.Nodes;

namespace WorkPilot.Domain.Automation;

/// <summary>Per-run budget envelope (RUN-A13). Stored as canonical JSON.</summary>
public sealed record RunBudget(
    int MaxModelTurns,
    long MaxTotalTokens,
    long MaxWallClockSeconds,
    int MaxCapabilityCalls,
    long MaxResultBytes)
{
    /// <summary>Zero budget: every reservation fails. Used for dry environments / tests.</summary>
    public static readonly RunBudget None = new(0, 0, 0, 0, 0);

    public JsonNode ToCanonicalJson() => new JsonObject
    {
        ["max_model_turns"] = MaxModelTurns,
        ["max_total_tokens"] = MaxTotalTokens,
        ["max_wall_clock_seconds"] = MaxWallClockSeconds,
        ["max_capability_calls"] = MaxCapabilityCalls,
        ["max_result_bytes"] = MaxResultBytes
    };

    public static RunBudget FromJson(JsonNode node) => new(
        JsonParsing.GetLong(node, "max_model_turns") is { } a ? (int)a : 0,
        JsonParsing.GetLong(node, "max_total_tokens") ?? 0,
        JsonParsing.GetLong(node, "max_wall_clock_seconds") ?? 0,
        JsonParsing.GetLong(node, "max_capability_calls") is { } c ? (int)c : 0,
        JsonParsing.GetLong(node, "max_result_bytes") ?? 0);
}
