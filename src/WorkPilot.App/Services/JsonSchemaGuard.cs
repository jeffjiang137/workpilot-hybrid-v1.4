using System.Text.Json;

namespace WorkPilot.Services;

public static class JsonSchemaGuard
{
    public static JsonElement ValidateObject(string schemaJson, string argumentsJson)
    {
        if (argumentsJson.Length > 1024 * 1024) throw new ArgumentException("能力参数超过 1 MiB");
        using var arguments = JsonDocument.Parse(argumentsJson, new JsonDocumentOptions { MaxDepth = 64 });
        if (arguments.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("能力参数必须是 JSON 对象");
        using var schema = JsonDocument.Parse(schemaJson, new JsonDocumentOptions { MaxDepth = 64 });
        var root = schema.RootElement;
        var properties = root.TryGetProperty("properties", out var propertyValue) && propertyValue.ValueKind == JsonValueKind.Object
            ? propertyValue : default;
        HashSet<string> required = root.TryGetProperty("required", out var requiredValue) && requiredValue.ValueKind == JsonValueKind.Array
            ? requiredValue.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in required)
            if (!arguments.RootElement.TryGetProperty(name, out _)) throw new ArgumentException($"缺少必需参数：{name}");
        foreach (var argument in arguments.RootElement.EnumerateObject())
        {
            if (properties.ValueKind != JsonValueKind.Object || !properties.TryGetProperty(argument.Name, out var definition))
                throw new ArgumentException($"不支持参数：{argument.Name}");
            ValidateType(argument.Name, argument.Value, definition);
        }
        return arguments.RootElement.Clone();
    }

    private static void ValidateType(string name, JsonElement value, JsonElement definition)
    {
        if (!definition.TryGetProperty("type", out var type)) return;
        string[] accepted = type.ValueKind == JsonValueKind.String ? [type.GetString()!] :
            type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : [];
        var actual = value.ValueKind switch
        {
            JsonValueKind.String => "string", JsonValueKind.Number when value.TryGetInt64(out _) => "integer",
            JsonValueKind.Number => "number", JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Array => "array", JsonValueKind.Object => "object", JsonValueKind.Null => "null", _ => "unknown"
        };
        if (!accepted.Contains(actual, StringComparer.Ordinal) && !(actual == "integer" && accepted.Contains("number", StringComparer.Ordinal)))
            throw new ArgumentException($"参数 {name} 类型错误，应为 {string.Join('/', accepted)}");
        if (value.ValueKind == JsonValueKind.String && definition.TryGetProperty("maxLength", out var max) &&
            max.TryGetInt32(out var maximum) && (value.GetString()?.Length ?? 0) > maximum)
            throw new ArgumentException($"参数 {name} 超过最大长度 {maximum}");
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) &&
            definition.TryGetProperty("minimum", out var min) && min.TryGetInt64(out var minimum) && number < minimum)
            throw new ArgumentException($"参数 {name} 小于最小值 {minimum}");
    }
}
