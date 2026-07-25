using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using WorkPilot.Contracts.Primitives;

namespace WorkPilot.Domain.Automation.Run.Interpreter;

/// <summary>
/// Run-scoped variable store for workflow interpretation (spec doc 03 §4). Holds three immutable
/// roots — <c>trigger.*</c>, <c>run.*</c>, <c>system.*</c> — plus the mutable <c>vars.*</c> namespace
/// written by executed nodes' <c>output_key</c>. <c>secrets.*</c> is never permitted: the store
/// rejects any declaration or resolution under that root, guaranteeing no secret ever enters a
/// variable (doc 03 §4: "秘密永不进入变量").
/// </summary>
public sealed class VariableStore
{
    private readonly Dictionary<string, JsonNode> _vars = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonNode> _roots;

    public VariableStore(IReadOnlyDictionary<string, JsonNode>? triggerVars = null,
        IReadOnlyDictionary<string, JsonNode>? runVars = null,
        IReadOnlyDictionary<string, JsonNode>? systemVars = null)
    {
        _roots = new Dictionary<string, JsonNode>(StringComparer.Ordinal)
        {
            ["trigger"] = ToObject(triggerVars),
            ["run"] = ToObject(runVars),
            ["system"] = ToObject(systemVars)
        };
    }

    private static JsonObject ToObject(IReadOnlyDictionary<string, JsonNode>? map)
    {
        var obj = new JsonObject();
        if (map is not null)
            foreach (var kv in map)
                obj[kv.Key] = kv.Value?.DeepClone();
        return obj;
    }

    /// <summary>Declares a node output under <c>vars.&lt;key&gt;</c>. Throws if the key is reserved or malformed.</summary>
    public void Declare(string nodeId, string outputKey, JsonNode value)
    {
        if (string.IsNullOrWhiteSpace(outputKey))
            throw new DomainException(RunErrors.VariableBindingFailedError(nodeId, "(empty key)"));
        if (ReservedOrInvalid(outputKey))
            throw new DomainException(RunErrors.VariableBindingFailedError(nodeId, $"vars.{outputKey}"));
        _vars[outputKey] = value?.DeepClone() ?? JsonValue.Create((string?)null)!;
    }

    /// <summary>
    /// Resolves a <c>$ref</c> path such as <c>vars.summary</c>, <c>trigger.project.owner</c> or
    /// <c>run.id</c>. Returns false if the root/key is unknown or reserved. Never throws for a
    /// missing path; the interpreter decides whether a missing binding is fatal.
    /// </summary>
    public bool TryResolve(string refPath, out JsonNode? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(refPath)) return false;

        var parts = refPath.Split('.');
        if (parts.Length < 2) return false;

        var root = parts[0];
        if (root == "secrets") return false; // never readable from variables

        if (root == "vars")
        {
            // vars.<key>: the namespace is flat — the key is exactly parts[1], resolved directly.
            if (parts.Length != 2) return false;
            if (!_vars.TryGetValue(parts[1], out var v) || v is null) return false;
            value = v.DeepClone();
            return true;
        }

        if (root is "trigger" or "run" or "system")
        {
            if (!_roots.TryGetValue(root, out var cursor) || cursor is null) return false;
            for (var i = 1; i < parts.Length; i++)
            {
                if (cursor is not JsonObject obj) return false;
                if (!obj.TryGetPropertyValue(parts[i], out var next) || next is null) return false;
                cursor = next;
            }
            value = cursor.DeepClone();
            return true;
        }

        return false;
    }

    /// <summary>The current set of declared <c>vars.*</c> keys (for diagnostics / tests).</summary>
    public IReadOnlyCollection<string> DeclaredKeys => _vars.Keys;

    private static bool ReservedOrInvalid(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return true;
        if (key.Length > 32) return true;
        if (!char.IsLower(key[0])) return true;
        foreach (var c in key)
            if (!(char.IsLower(c) || char.IsDigit(c) || c == '_')) return true;
        return key is "trigger" or "run" or "system" or "secrets";
    }
}
