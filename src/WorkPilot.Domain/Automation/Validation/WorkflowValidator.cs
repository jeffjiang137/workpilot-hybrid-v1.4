using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WorkPilot.Contracts.Primitives;

namespace WorkPilot.Domain.Automation.Validation;

/// <summary>
/// Pure validator for <see cref="WorkflowDefinition"/> (AUT-004). Enforces the structural and
/// data-flow rules from spec doc 03 §5. Produces a stable, sorted <see cref="ValidationResult"/>
/// shared by the editor preview, the enable Preflight, and the materializer. No I/O, no static
/// clock/random — fully deterministic for a given definition.
/// </summary>
public static class WorkflowValidator
{
    private static readonly Regex NodeIdPattern = new("^[a-z][a-z0-9_]{0,31}$", RegexOptions.Compiled);
    private static readonly HashSet<string> ReservedVarRoots = new() { "trigger", "run", "secrets", "system" };
    private static readonly HashSet<string> KnownKinds = new()
        { "agent_prompt", "capability_call", "condition", "delay", "notification" };

    public static ValidationResult Validate(WorkflowDefinition workflow)
    {
        var issues = new List<ValidationIssue>();

        var nodes = workflow.Nodes;
        var edges = workflow.Edges;

        // Node / edge counts (spec doc 03 §5 #2)
        if (nodes.Count == 0)
            issues.Add(ValidationCodes.Error(ValidationCodes.WorkflowEmpty, "/workflow/nodes"));
        if (nodes.Count > Limits.V1_5.MaxWorkflowNodes)
            issues.Add(ValidationCodes.Error(ValidationCodes.NodeCountExceeded, "/workflow/nodes",
                KV("max", Limits.V1_5.MaxWorkflowNodes.ToString()), KV("count", nodes.Count.ToString())));
        if (edges.Count > Limits.V1_5.MaxWorkflowEdges)
            issues.Add(ValidationCodes.Error(ValidationCodes.EdgeCountExceeded, "/workflow/edges",
                KV("max", Limits.V1_5.MaxWorkflowEdges.ToString()), KV("count", edges.Count.ToString())));

        // Node identity + per-node field validation
        var allIds = new HashSet<string>(StringComparer.Ordinal);
        var duplicateIds = new HashSet<string>(StringComparer.Ordinal);
        var nodeIndex = new Dictionary<string, WorkflowNode>(StringComparer.Ordinal);
        var nodeOrder = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            var ptr = $"/workflow/nodes/{i}";
            if (string.IsNullOrEmpty(n.NodeId) || !NodeIdPattern.IsMatch(n.NodeId) || n.NodeId.Length > Limits.V1_5.MaxNodeIdLength)
                issues.Add(ValidationCodes.Error(ValidationCodes.NodeIdInvalid, $"{ptr}/node_id", KV("node_id", n.NodeId)));
            else if (!allIds.Add(n.NodeId))
                duplicateIds.Add(n.NodeId);
            if (!string.IsNullOrEmpty(n.NodeId)) { nodeIndex[n.NodeId] = n; nodeOrder[n.NodeId] = i; }

            if (n.DisplayName.Length < 1 || n.DisplayName.Length > Limits.V1_5.MaxWorkflowNodeDisplayNameLength)
                issues.Add(ValidationCodes.Error(ValidationCodes.NodeDisplayNameInvalid, $"{ptr}/display_name",
                    KV("node_id", n.NodeId), KV("len", n.DisplayName.Length.ToString())));
            if (n.TimeoutSeconds < Limits.V1_5.MinWorkflowNodeTimeoutSeconds || n.TimeoutSeconds > Limits.V1_5.MaxWorkflowNodeTimeoutSeconds)
                issues.Add(ValidationCodes.Error(ValidationCodes.NodeTimeoutInvalid, $"{ptr}/timeout_seconds",
                    KV("node_id", n.NodeId), KV("value", n.TimeoutSeconds.ToString())));
            if (!KnownKinds.Contains(n.Kind))
                issues.Add(ValidationCodes.Error(ValidationCodes.NodeKindInvalid, $"{ptr}/kind",
                    KV("node_id", n.NodeId), KV("kind", n.Kind)));
            ValidateRetryPolicy(n.Payload, ptr, issues);
        }
        foreach (var dup in duplicateIds)
            issues.Add(ValidationCodes.Error(ValidationCodes.NodeIdDuplicate, "/workflow/nodes",
                KV("node_id", dup)));

        // Edge referential integrity + branch values
        var enabledEdges = new List<WorkflowEdge>();
        for (var i = 0; i < edges.Count; i++)
        {
            var e = edges[i];
            var ptr = $"/workflow/edges/{i}";
            if (!nodeIndex.ContainsKey(e.FromNodeId))
                issues.Add(ValidationCodes.Error(ValidationCodes.NodeIdInvalid, $"{ptr}/from_node_id", KV("value", e.FromNodeId)));
            if (!nodeIndex.ContainsKey(e.ToNodeId))
                issues.Add(ValidationCodes.Error(ValidationCodes.NodeIdInvalid, $"{ptr}/to_node_id", KV("value", e.ToNodeId)));
            if (e.Branch != "next" && e.Branch != "true" && e.Branch != "false")
                issues.Add(ValidationCodes.Error(ValidationCodes.ConditionBranchInvalid, $"{ptr}/branch", KV("branch", e.Branch)));
            if (nodeIndex.ContainsKey(e.FromNodeId) && nodeIndex.ContainsKey(e.ToNodeId) && !nodeIndex[e.FromNodeId].Disabled && !nodeIndex[e.ToNodeId].Disabled)
                enabledEdges.Add(e);
        }

        // Entry
        if (string.IsNullOrEmpty(workflow.EntryNodeId) || !nodeIndex.ContainsKey(workflow.EntryNodeId))
        {
            issues.Add(ValidationCodes.Error(ValidationCodes.EntryNotFound, "/workflow/entry_node_id",
                KV("entry_node_id", workflow.EntryNodeId)));
        }
        else
        {
            var entryInDegree = enabledEdges.Count(e => e.ToNodeId == workflow.EntryNodeId);
            if (entryInDegree != 0)
                issues.Add(ValidationCodes.Error(ValidationCodes.EntryInDegreeNonZero, "/workflow/entry_node_id",
                    KV("in_degree", entryInDegree.ToString())));
        }

        if (issues.Any(i => i.Severity == ValidationSeverity.Error))
            return new ValidationResult(issues);

        // Enabled graph
        var enabledIds = nodeIndex.Where(kv => !kv.Value.Disabled).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
        var successors = new Dictionary<string, List<WorkflowEdge>>(StringComparer.Ordinal);
        var predecessors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var id in enabledIds)
        {
            successors[id] = new List<WorkflowEdge>();
            predecessors[id] = new List<string>();
        }
        foreach (var e in enabledEdges)
        {
            successors[e.FromNodeId].Add(e);
            predecessors[e.ToNodeId].Add(e.FromNodeId);
        }

        // Out-degree / condition branch rules (spec doc 03 §5 #6)
        foreach (var id in enabledIds)
        {
            var outs = successors[id];
            var ptr = $"/workflow/nodes/{nodeOrder[id]}";
            if (nodeIndex[id].Kind == "condition")
            {
                var branches = outs.Select(o => o.Branch).ToHashSet(StringComparer.Ordinal);
                if (outs.Count != 2 || !branches.Contains("true") || !branches.Contains("false"))
                    issues.Add(ValidationCodes.Error(ValidationCodes.ConditionBranchInvalid, $"{ptr}/kind",
                        KV("node_id", id), KV("out_degree", outs.Count.ToString())));
            }
            else if (outs.Count > 1)
            {
                issues.Add(ValidationCodes.Error(ValidationCodes.NodeOutDegreeInvalid, $"{ptr}/kind",
                    KV("node_id", id), KV("out_degree", outs.Count.ToString())));
            }
        }

        // Kahn topological sort over enabled nodes (spec doc 03 §5 #4)
        var inDegree = enabledIds.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        foreach (var e in enabledEdges)
            inDegree[e.ToNodeId]++;
        var queue = new Queue<string>(enabledIds.Where(id => inDegree[id] == 0));
        var processed = new HashSet<string>(StringComparer.Ordinal);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            processed.Add(id);
            foreach (var e in successors[id])
                if (--inDegree[e.ToNodeId] == 0) queue.Enqueue(e.ToNodeId);
        }
        if (processed.Count < enabledIds.Count)
        {
            var cyclic = enabledIds.Except(processed).ToList();
            foreach (var id in cyclic)
            {
                var ptr = $"/workflow/nodes/{nodeOrder[id]}";
                issues.Add(ValidationCodes.Error(ValidationCodes.WorkflowCycle, $"{ptr}/node_id", KV("node_id", id)));
            }
        }

        // Reachability from entry + variable data-flow (spec doc 03 §5 #5, #8)
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        if (nodeIndex.ContainsKey(workflow.EntryNodeId) && !nodeIndex[workflow.EntryNodeId].Disabled)
        {
            var bfs = new Queue<string>();
            bfs.Enqueue(workflow.EntryNodeId);
            reachable.Add(workflow.EntryNodeId);
            while (bfs.Count > 0)
            {
                var id = bfs.Dequeue();
                foreach (var e in successors[id])
                    if (reachable.Add(e.ToNodeId)) bfs.Enqueue(e.ToNodeId);
            }
        }
        foreach (var id in enabledIds.Except(reachable))
        {
            var ptr = $"/workflow/nodes/{nodeOrder[id]}";
            issues.Add(ValidationCodes.Error(ValidationCodes.WorkflowUnreachable, $"{ptr}/node_id", KV("node_id", id)));
        }

        // At least one terminal node among enabled (spec doc 03 §5 #7)
        var hasTerminal = enabledIds.Any(id => successors[id].Count == 0);
        if (!hasTerminal && enabledIds.Count > 0)
            issues.Add(ValidationCodes.Error(ValidationCodes.WorkflowNoTerminal, "/workflow/nodes"));

        // Variable availability: each node may reference only vars declared by its ancestors
        // (or the always-available trigger/run roots).
        var outputKeys = new Dictionary<string, string>(StringComparer.Ordinal); // nodeId -> output var
        foreach (var id in enabledIds)
        {
            var key = ExtractOutputKey(nodeIndex[id]);
            if (key is not null)
            {
                if (!NodeIdPattern.IsMatch(key) || key.Length > Limits.V1_5.MaxVariableNameLength || ReservedVarRoots.Contains(key))
                    issues.Add(ValidationCodes.Error(ValidationCodes.VariableOutputKeyInvalid,
                        $"/workflow/nodes/{nodeOrder[id]}/payload/output_key", KV("node_id", id), KV("key", key)));
                else
                    outputKeys[id] = key;
            }
        }

        // ancestors(node) = nodes that can reach it (reverse BFS)
        foreach (var id in enabledIds)
        {
            var ancestors = new HashSet<string>(StringComparer.Ordinal);
            var rb = new Queue<string>(predecessors[id]);
            while (rb.Count > 0)
            {
                var a = rb.Dequeue();
                if (ancestors.Add(a))
                    foreach (var p in predecessors[a]) rb.Enqueue(p);
            }
            var available = new HashSet<string>(StringComparer.Ordinal);
            foreach (var a in ancestors)
                if (outputKeys.TryGetValue(a, out var v)) available.Add(v);

            var refs = ExtractVarReferences(nodeIndex[id]);
            var unknown = refs.Where(r => !available.Contains(r)).ToList();
            if (unknown.Count > 0)
            {
                var ptr = $"/workflow/nodes/{nodeOrder[id]}";
                issues.Add(ValidationCodes.Error(ValidationCodes.VariableNotAvailable, ptr,
                    KV("node_id", id), KV("var", string.Join(",", unknown))));
            }
        }

        return new ValidationResult(issues);
    }

    private static void ValidateRetryPolicy(JsonObject? payload, string ptr, List<ValidationIssue> issues)
    {
        if (payload is null) return;
        var rp = payload["retry_policy"] as JsonObject;
        if (rp is null) return;
        var max = JsonParsing.GetLong(rp, "max_attempts");
        var baseD = JsonParsing.GetLong(rp, "base_delay_seconds");
        var maxD = JsonParsing.GetLong(rp, "max_delay_seconds");
        if (max is < Limits.V1_5.MaxRetryMaxAttempts or null)
            issues.Add(ValidationCodes.Error(ValidationCodes.RetryPolicyInvalid, $"{ptr}/payload/retry_policy/max_attempts", KV("value", (max ?? -1).ToString())));
        if (baseD is > Limits.V1_5.MaxRetryBaseDelaySeconds or null)
            issues.Add(ValidationCodes.Error(ValidationCodes.RetryPolicyInvalid, $"{ptr}/payload/retry_policy/base_delay_seconds", KV("value", (baseD ?? -1).ToString())));
        if (maxD is > Limits.V1_5.MaxRetryMaxDelaySeconds or null)
            issues.Add(ValidationCodes.Error(ValidationCodes.RetryPolicyInvalid, $"{ptr}/payload/retry_policy/max_delay_seconds", KV("value", (maxD ?? -1).ToString())));
    }

    private static string? ExtractOutputKey(WorkflowNode node)
    {
        if (node.Kind != "agent_prompt" && node.Kind != "capability_call") return null;
        var key = JsonParsing.GetString(node.Payload, "output_key");
        return string.IsNullOrEmpty(key) ? null : key;
    }

    /// <summary>Collects the <c>vars.&lt;name&gt;</c> roots referenced by a node's payload.</summary>
    private static HashSet<string> ExtractVarReferences(WorkflowNode node)
    {
        var refs = new HashSet<string>(StringComparer.Ordinal);
        if (node.Payload is null) return refs;

        // agent_prompt.input_bindings: object of name -> { "$ref": "vars.x..." }
        if (node.Payload["input_bindings"] is JsonObject ib)
            foreach (var kv in ib)
                if (kv.Value is JsonObject refObj && JsonParsing.GetString(refObj, "$ref") is { } r)
                    AddVarRef(refs, r);

        // capability_call.arguments_template: arbitrary JSON possibly containing "$ref"
        if (node.Payload["arguments_template"] is JsonNode args)
            CollectRefs(args, refs);

        // condition.condition: AST with leaf "path": "vars.x..." or "trigger.*"/"run.*"
        if (node.Payload["condition"] is JsonNode cond)
            CollectPaths(cond, refs);

        // notification body/title templates may reference vars.<safe>
        if (node.Payload["body_template"] is { } bt) AddVarRef(refs, bt.GetValue<string>());
        if (node.Payload["title_template"] is { } tt) AddVarRef(refs, tt.GetValue<string>());

        return refs;
    }

    private static void AddVarRef(HashSet<string> refs, string? s)
    {
        if (string.IsNullOrEmpty(s)) return;
        if (s.StartsWith("vars.", StringComparison.Ordinal))
        {
            var dot = s.IndexOf('.', 5);
            var name = dot < 0 ? s.Substring(5) : s.Substring(5, dot - 5);
            if (name.Length > 0) refs.Add(name);
        }
    }

    private static void CollectRefs(JsonNode node, HashSet<string> refs)
    {
        if (node is JsonObject obj)
        {
            foreach (var kv in obj)
            {
                if (kv.Key == "$ref" && kv.Value is { } val && val.GetValueKind() == System.Text.Json.JsonValueKind.String)
                    AddVarRef(refs, val.GetValue<string>());
                else if (kv.Value is not null)
                    CollectRefs(kv.Value, refs);
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr)
                if (item is not null) CollectRefs(item, refs);
        }
    }

    private static void CollectPaths(JsonNode node, HashSet<string> refs)
    {
        if (node is not JsonObject obj) return;
        if (obj["path"] is { } p && p.GetValueKind() == System.Text.Json.JsonValueKind.String)
            AddVarRef(refs, p.GetValue<string>());
        if (obj["all"] is JsonArray all) foreach (var c in all) if (c is not null) CollectPaths(c, refs);
        if (obj["any"] is JsonArray any) foreach (var c in any) if (c is not null) CollectPaths(c, refs);
        if (obj["not"] is { } not) CollectPaths(not, refs);
    }

    private static (string Key, string Value) KV(string k, string v) => (k, v);
}
