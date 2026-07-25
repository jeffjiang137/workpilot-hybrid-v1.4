using System.Text.Json.Nodes;
using WorkPilot.Domain.Automation;

namespace WorkPilot.Domain.Tests.Validation;

/// <summary>Builders for <see cref="WorkflowDefinition"/> / <see cref="WorkflowNode"/> in validator tests.</summary>
internal static class Wf
{
    public static JsonObject Bindings(params (string Name, string Path)[] refs)
    {
        var obj = new JsonObject();
        foreach (var (name, path) in refs)
            obj[name] = new JsonObject { ["$ref"] = path };
        return obj;
    }

    public static JsonObject Condition(params (string Path, string Op)[] leaves)
    {
        var arr = new JsonArray();
        foreach (var (path, op) in leaves)
            arr.Add(new JsonObject { ["path"] = path, ["op"] = op });
        return new JsonObject { ["all"] = arr };
    }

    public static WorkflowNode Agent(string id, string? outputKey = null,
        JsonObject? inputBindings = null, int timeout = 60, bool disabled = false) => new(id, id, "agent_prompt", timeout, disabled,
        new JsonObject
        {
            ["output_key"] = outputKey,
            ["input_bindings"] = inputBindings,
            ["instruction_template"] = "do something",
            ["max_model_turns"] = 1,
            ["capability_mode"] = "none"
        });

    public static WorkflowNode ConditionNode(string id, JsonObject condition, int timeout = 60, bool disabled = false) => new(id, id, "condition", timeout, disabled,
        new JsonObject { ["condition"] = condition });

    public static WorkflowNode Delay(string id, int delaySeconds = 120, int timeout = 60, bool disabled = false) => new(id, id, "delay", timeout, disabled,
        new JsonObject { ["delay_seconds"] = delaySeconds });

    public static WorkflowNode Notification(string id, int timeout = 60, bool disabled = false) => new(id, id, "notification", timeout, disabled,
        new JsonObject { ["title_template"] = "t", ["body_template"] = "b", ["required"] = false });

    public static WorkflowEdge Edge(string from, string to, string branch = "next") => new(from, to, branch);

    public static WorkflowDefinition Of(string entry, params WorkflowNode[] nodes) =>
        new(1, entry, nodes, Array.Empty<WorkflowEdge>());

    public static WorkflowDefinition Of(string entry, WorkflowNode[] nodes, WorkflowEdge[] edges) =>
        new(1, entry, nodes, edges);
}
