using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WorkPilot.Contracts.Primitives;

/// <summary>
/// JSON Canonicalization Scheme (RFC 8785) 风格的规范化器。
/// 对象键按 UTF-16 码元序排序、数组保留顺序、数字最短规范化。
/// 规范化结果确定性可复现，供 Revision 计算 canonical_sha256 与后续校验复用。
/// 纯算法、零依赖，置于 Contracts 以便 Domain（不可引用 Infrastructure）也能计算规范化哈希。
/// </summary>
public static class JcsCanonicalizer
{
    public static string Canonicalize(string json)
    {
        using var document = JsonDocument.Parse(json);
        var builder = new StringBuilder();
        WriteNode(document.RootElement, builder);
        return builder.ToString();
    }

    public static string Canonicalize(JsonNode? node) => Canonicalize(node?.ToJsonString() ?? "null");

    public static byte[] CanonicalizeToUtf8(string json) => Encoding.UTF8.GetBytes(Canonicalize(json));

    /// <summary>规范化给定 JSON 节点并计算 SHA-256 十六进制（小写）。用于 Revision 的不可变内容指纹。</summary>
    public static string CanonicalizeToSha256(JsonNode? node)
    {
        var canonical = Canonicalize(node);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static void WriteNode(JsonElement element, StringBuilder builder)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var properties = element.EnumerateObject().ToList();
                properties.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                builder.Append('{');
                for (var i = 0; i < properties.Count; i++)
                {
                    if (i > 0) builder.Append(',');
                    WriteString(properties[i].Name, builder);
                    builder.Append(':');
                    WriteNode(properties[i].Value, builder);
                }

                builder.Append('}');
                break;
            case JsonValueKind.Array:
                builder.Append('[');
                var first = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!first) builder.Append(',');
                    first = false;
                    WriteNode(item, builder);
                }

                builder.Append(']');
                break;
            case JsonValueKind.String:
                WriteString(element.GetString() ?? string.Empty, builder);
                break;
            case JsonValueKind.Number:
                builder.Append(CanonicalizeNumber(element.GetRawText()));
                break;
            case JsonValueKind.True:
                builder.Append("true");
                break;
            case JsonValueKind.False:
                builder.Append("false");
                break;
            case JsonValueKind.Null:
                builder.Append("null");
                break;
            default:
                builder.Append("null");
                break;
        }
    }

    private static void WriteString(string value, StringBuilder builder)
    {
        builder.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '\u2028':
                    builder.Append("\\u2028");
                    break;
                case '\u2029':
                    builder.Append("\\u2029");
                    break;
                default:
                    if (character < 0x20)
                        builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        builder.Append(character);
                    break;
            }
        }

        builder.Append('"');
    }

    private static string CanonicalizeNumber(string raw)
    {
        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
            return integer.ToString(CultureInfo.InvariantCulture);

        var normalized = raw.Replace('E', 'e');
        var exponentIndex = normalized.IndexOf('e');
        if (exponentIndex >= 0)
        {
            var mantissa = normalized[..exponentIndex];
            var exponent = normalized[(exponentIndex + 1)..].TrimStart('+');
            normalized = mantissa + "e" + exponent;
        }

        return normalized;
    }
}
