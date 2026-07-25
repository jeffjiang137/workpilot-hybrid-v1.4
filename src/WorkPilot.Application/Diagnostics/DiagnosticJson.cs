using System.Collections.Generic;
using System.Globalization;
using System.Text;
using WorkPilot.Contracts.Primitives;

namespace WorkPilot.Application.Diagnostics;

/// <summary>Builds a safe, shallow JSON object from diagnostic <c>safe</c> bags (scalars only, manually escaped).</summary>
internal static class DiagnosticJson
{
    public static string BuildSafeObject(IReadOnlyDictionary<string, object?>? safe)
    {
        if (safe is null || safe.Count == 0) return "{}";
        var sb = new StringBuilder();
        sb.Append('{');
        var first = true;
        foreach (var (k, v) in safe)
        {
            if (k is null) continue;
            if (!TryToken(v, out var token)) continue; // drop non-scalar defensively
            if (!first) sb.Append(',');
            first = false;
            sb.Append('"').Append(Escape(k)).Append("\":");
            sb.Append(token);
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static bool TryToken(object? v, out string token)
    {
        token = "";
        switch (v)
        {
            case null: return false;
            case bool b: token = b ? "true" : "false"; return true;
            case string s: token = '"' + Escape(s) + '"'; return true;
            case long or int or short or byte: token = Convert.ToString(v, CultureInfo.InvariantCulture)!; return true;
            default: return false;
        }
    }

    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    sb.Append(c < 0x20 ? "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture) : c.ToString());
                    break;
            }
        }
        return sb.ToString();
    }
}
