using System;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WorkPilot.Domain.Automation;
using WorkPilot.Domain.Automation.Run;
using WorkPilot.Domain.Automation.Run.Interpreter;

namespace WorkPilot.Application.Automation.Run.Executors;

/// <summary>
/// Strict <c>{{$ref:path}}</c> template substitution shared by the agent and notification executors.
/// Only references that pass the caller-supplied <paramref name="isAllowed"/> predicate are substituted;
/// any disallowed or unresolvable reference fails the render (fail-closed, no empty-value fallback —
/// doc 03 §4: "解析失败为 VariableBindingFailed，不传空值继续"). The <see cref="VariableStore"/> itself
/// rejects the <c>secrets</c> root, so a secret can never be referenced here.
/// </summary>
internal static class TemplateRenderer
{
    private static readonly Regex RefPattern = new(@"\{\{\$ref:([^}]+)\}\}", RegexOptions.Compiled);

    /// <summary>
    /// Substitutes every <c>{{$ref:path}}</c> token. Returns null (and <paramref name="badRef"/>) if any
    /// token is disallowed or cannot be resolved. Literal text between tokens is preserved verbatim.
    /// </summary>
    public static string? Render(string template, VariableStore variables, Func<string, bool> isAllowed, out string? badRef)
    {
        badRef = null;
        if (string.IsNullOrEmpty(template)) return string.Empty;

        var sb = new StringBuilder();
        var last = 0;
        foreach (Match m in RefPattern.Matches(template))
        {
            var path = m.Groups[1].Value.Trim();
            sb.Append(template, last, m.Index - last);
            if (!isAllowed(path) || !variables.TryResolve(path, out var value) || value is null)
            {
                badRef = path;
                return null;
            }
            sb.Append(Stringify(value));
            last = m.Index + m.Length;
        }
        sb.Append(template, last, template.Length - last);
        return sb.ToString();
    }

    internal static string Stringify(JsonNode? n) => n switch
    {
        null => "null",
        JsonValue v => v.ToJsonString().Trim('"'),
        _ => n.ToJsonString()
    };
}
