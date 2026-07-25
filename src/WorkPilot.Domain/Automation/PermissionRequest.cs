using System.Text.Json.Nodes;

namespace WorkPilot.Domain.Automation;

/// <summary>Declared capability permissions for a revision. Scope defaults to read-only. Stored as canonical JSON.</summary>
public sealed record PermissionRequest(IReadOnlyList<string> CapabilityStableIds, string Scope)
{
    public JsonNode ToCanonicalJson() => new JsonObject
    {
        ["capability_stable_ids"] = new JsonArray(CapabilityStableIds.Select(x => (JsonNode)x).ToArray()),
        ["scope"] = Scope
    };

    public static PermissionRequest FromJson(JsonNode node)
    {
        var ids = (node["capability_stable_ids"] as JsonArray)?
            .Select(x => x!.GetValue<string>()).ToList()
            ?? new System.Collections.Generic.List<string>();
        return new PermissionRequest(ids, JsonParsing.GetString(node, "scope") ?? "read-only");
    }
}
