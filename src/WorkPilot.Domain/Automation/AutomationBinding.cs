using System.Text.Json.Nodes;

namespace WorkPilot.Domain.Automation;

/// <summary>Binds an automation to a project and a (frozen) expert. Stored as canonical JSON.</summary>
public sealed record AutomationBinding(string? ProjectId, string? ExpertId)
{
    public JsonNode ToCanonicalJson() => new JsonObject
    {
        ["project_id"] = (JsonNode?)ProjectId,
        ["expert_id"] = (JsonNode?)ExpertId
    };

    public static AutomationBinding FromJson(JsonNode node) => new(
        JsonParsing.GetString(node, "project_id"),
        JsonParsing.GetString(node, "expert_id"));
}
