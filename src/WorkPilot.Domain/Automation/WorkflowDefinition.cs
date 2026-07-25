using System.Text.Json.Nodes;

namespace WorkPilot.Domain.Automation;

public sealed record WorkflowNode(
    string NodeId,
    string DisplayName,
    string Kind,
    int TimeoutSeconds,
    bool Disabled,
    JsonObject? Payload)
{
    public JsonNode ToCanonicalJson() => new JsonObject
    {
        ["node_id"] = NodeId,
        ["display_name"] = DisplayName,
        ["kind"] = Kind,
        ["timeout_seconds"] = TimeoutSeconds,
        ["disabled"] = Disabled,
        ["payload"] = (JsonNode?)Payload?.DeepClone()
    };

    public static WorkflowNode FromJson(JsonNode node) => new(
        JsonParsing.GetString(node, "node_id") ?? string.Empty,
        JsonParsing.GetString(node, "display_name") ?? string.Empty,
        JsonParsing.GetString(node, "kind") ?? string.Empty,
        JsonParsing.GetLong(node, "timeout_seconds") is { } t ? (int)t : 0,
        JsonParsing.GetBool(node, "disabled", false),
        node["payload"] as JsonObject);
}

public sealed record WorkflowEdge(string FromNodeId, string ToNodeId, string Branch)
{
    public JsonNode ToCanonicalJson() => new JsonObject
    {
        ["from_node_id"] = FromNodeId,
        ["to_node_id"] = ToNodeId,
        ["branch"] = Branch
    };

    public static WorkflowEdge FromJson(JsonNode node) => new(
        JsonParsing.GetString(node, "from_node_id") ?? string.Empty,
        JsonParsing.GetString(node, "to_node_id") ?? string.Empty,
        JsonParsing.GetString(node, "branch") ?? "next");
}

public sealed record WorkflowDefinition(
    int SchemaVersion,
    string EntryNodeId,
    IReadOnlyList<WorkflowNode> Nodes,
    IReadOnlyList<WorkflowEdge> Edges)
{
    public JsonNode ToCanonicalJson()
    {
        var nodes = new JsonArray(Nodes.Select(n => n.ToCanonicalJson()).ToArray());
        var edges = new JsonArray(Edges.Select(e => e.ToCanonicalJson()).ToArray());
        return new JsonObject
        {
            ["schema_version"] = SchemaVersion,
            ["entry_node_id"] = EntryNodeId,
            ["nodes"] = nodes,
            ["edges"] = edges
        };
    }

    public static WorkflowDefinition FromJson(JsonNode node)
    {
        var nodes = (node["nodes"] as JsonArray)?.Select(x => WorkflowNode.FromJson(x!)).ToList()
                    ?? new System.Collections.Generic.List<WorkflowNode>();
        var edges = (node["edges"] as JsonArray)?.Select(x => WorkflowEdge.FromJson(x!)).ToList()
                    ?? new System.Collections.Generic.List<WorkflowEdge>();
        return new WorkflowDefinition(
            JsonParsing.GetLong(node, "schema_version") is { } v ? (int)v : 1,
            JsonParsing.GetString(node, "entry_node_id") ?? string.Empty,
            nodes, edges);
    }
}
