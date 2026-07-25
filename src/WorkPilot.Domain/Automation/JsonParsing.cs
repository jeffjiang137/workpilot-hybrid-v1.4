using System.Text.Json.Nodes;

namespace WorkPilot.Domain.Automation;

/// <summary>Small null-tolerant helpers for reading canonical JSON produced by <c>ToCanonicalJson</c>.</summary>
internal static class JsonParsing
{
    public static string? GetString(JsonNode? node, string key)
    {
        var v = node?[key];
        return v is null ? null : v.GetValue<string>();
    }

    public static bool GetBool(JsonNode? node, string key, bool fallback)
    {
        var v = node?[key];
        return v is null ? fallback : v.GetValue<bool>();
    }

    public static long? GetLong(JsonNode? node, string key)
    {
        var v = node?[key];
        return v is null ? null : v.GetValue<long>();
    }

    public static int[]? GetIntArray(JsonNode? node, string key)
    {
        if (node?[key] is not JsonArray arr) return null;
        var list = new System.Collections.Generic.List<int>();
        foreach (var item in arr)
            if (item is not null) list.Add(item.GetValue<int>());
        return list.ToArray();
    }

    public static JsonArray? GetArray(JsonNode? node, string key) => node?[key] as JsonArray;
}
