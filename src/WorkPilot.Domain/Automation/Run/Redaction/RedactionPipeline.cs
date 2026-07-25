using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using WorkPilot.Contracts.Primitives;

namespace WorkPilot.Domain.Automation.Run.Redaction;

/// <summary>
/// Central redaction pipeline shared by Run Events, Security Audit and Diagnostic logs (doc 05 §4, ADR-1505).
/// Pure string processing — no I/O, no secret persistence. Stages run in fixed order:
/// 3) known-secret HMAC (cross-boundary/Unicode/case-exact), 4) header/URL parser, 5) pattern guard,
/// 6) length limit, 7) canary hook. (Stage 1 contract allowlist is enforced by the serializer; stage 2
/// key-classifier is defense-in-depth already covered by the allowlist.)
/// </summary>
public static class RedactionPipeline
{
    private static readonly Regex HeaderRegex = new(
        @"(?i)\b(Authorization|Cookie|Set-Cookie)\b\s*:[^\r\n;]*",
        RegexOptions.Compiled);

    private static readonly Regex UrlRegex = new(
        @"https?://[^\s""'<>]+",
        RegexOptions.Compiled);

    private static readonly Regex JwtRegex = new(
        @"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b",
        RegexOptions.Compiled);

    private static readonly Regex PemRegex = new(
        @"-----BEGIN [A-Z ]*PRIVATE KEY-----.*?-----END [A-Z ]*PRIVATE KEY-----",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex BearerRegex = new(
        @"(?i)\bBearer\s+[A-Za-z0-9._\-]+",
        RegexOptions.Compiled);

    private static readonly Regex BasicRegex = new(
        @"(?i)\bBasic\s+[A-Za-z0-9+/=]+",
        RegexOptions.Compiled);

    private static readonly Regex GhTokenRegex = new(
        @"\bgh[pousr]_[A-Za-z0-9]{20,}\b",
        RegexOptions.Compiled);

    /// <summary>
    /// Redacts a serialized record (the already allowlisted <c>safe_properties_json</c>, or a diagnostic
    /// <c>safe</c> object). <paramref name="matcher"/> detects registered secrets; <paramref name="canaryTokens"/>
    /// are test/support-bundle canaries that must never survive (LOG-A05). When <paramref name="releaseMode"/>
    /// is true and a canary survives, the whole value is replaced with a fixed fault marker.
    /// </summary>
    public static RedactionResult RedactSerialized(string input, ISecretMatcher? matcher, ISet<string>? canaryTokens, bool releaseMode)
    {
        var violations = new List<string>();
        var redacted = input ?? "";
        var redactionCount = 0;
        var truncated = false;

        // Stage 3: known-secret HMAC (whole text → catches cross-boundary splits)
        if (matcher != null)
        {
            var spans = matcher.Match(redacted);
            if (spans.Count > 0)
            {
                redacted = ApplySpans(redacted, spans, out var n);
                redactionCount += n;
            }
        }

        // Stage 4: header / URL parser
        redacted = HeaderRegex.Replace(redacted, m => { redactionCount++; return "[REDACTED:header]"; });
        redacted = UrlRegex.Replace(redacted, m =>
        {
            redactionCount++;
            return RedactUrl(m.Value);
        });

        // Stage 5: pattern guard
        redacted = JwtRegex.Replace(redacted, m => { redactionCount++; return "[REDACTED:jwt]"; });
        redacted = PemRegex.Replace(redacted, m => { redactionCount++; return "[REDACTED:pem]"; });
        redacted = BearerRegex.Replace(redacted, m => { redactionCount++; return "[REDACTED:bearer]"; });
        redacted = BasicRegex.Replace(redacted, m => { redactionCount++; return "[REDACTED:basic]"; });
        redacted = GhTokenRegex.Replace(redacted, m => { redactionCount++; return "[REDACTED:ghtoken]"; });

        // Stage 6: length limit
        if (redacted.Length > Limits.V1_5.MaxRedactionStringLength)
        {
            redacted = redacted.Substring(0, Limits.V1_5.MaxRedactionStringLength);
            truncated = true;
        }

        // Stage 7: canary hook
        if (canaryTokens is { Count: > 0 })
        {
            foreach (var c in canaryTokens)
            {
                if (!string.IsNullOrEmpty(c) && redacted.Contains(c, StringComparison.Ordinal))
                {
                    violations.Add("RUN_REDACTION_CANARY");
                    if (releaseMode)
                    {
                        redacted = "[REDACTION_FAILURE]";
                        violations.Add("RUN_REDACTION_FAILURE");
                    }
                    break;
                }
            }
        }

        return new RedactionResult(redacted, redactionCount, truncated, violations);
    }

    private static string ApplySpans(string text, IReadOnlyList<RedactionSpan> spans, out int count)
    {
        count = 0;
        if (spans.Count == 0) return text;
        var sorted = new List<RedactionSpan>(spans);
        sorted.Sort((a, b) => a.Start.CompareTo(b.Start));
        var sb = new StringBuilder();
        var cursor = 0;
        var coveredEnd = -1;
        foreach (var span in sorted)
        {
            if (span.Start < coveredEnd)
                continue; // overlapping with an already-redacted span
            if (span.Start > cursor)
                sb.Append(text, cursor, span.Start - cursor);
            sb.Append("[REDACTED:secret]");
            count++;
            cursor = span.Start + span.Length;
            coveredEnd = cursor;
        }
        if (cursor < text.Length)
            sb.Append(text, cursor, text.Length - cursor);
        return sb.ToString();
    }

    private static string RedactUrl(string url)
    {
        // strip query/fragment
        var q = url.IndexOf('?');
        if (q >= 0) url = url.Substring(0, q);
        var h = url.IndexOf('#');
        if (h >= 0) url = url.Substring(0, h);

        // strip userinfo (user:pass@)
        var at = url.IndexOf('@');
        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (at > schemeEnd && schemeEnd >= 0)
            url = url.Substring(0, schemeEnd + 3) + "[REDACTED]@" + url.Substring(at + 1);

        // keep scheme + host alias + path segment count
        if (schemeEnd < 0) return "[URL_REDACTED]";
        var afterScheme = schemeEnd + 3;
        var slash = url.IndexOf('/', afterScheme);
        var host = slash < 0 ? url.Substring(afterScheme) : url.Substring(afterScheme, slash - afterScheme);
        var pathCount = slash < 0 ? 0 : CountPathSegments(url.Substring(slash));
        var alias = HostAlias(host);
        return $"https://[{alias}]/<paths={pathCount}>";
    }

    private static int CountPathSegments(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/") return 0;
        var n = 0;
        foreach (var c in path)
            if (c == '/') n++;
        return n;
    }

    private static string HostAlias(string host)
    {
        try
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(host));
            var sb = new StringBuilder();
            for (var i = 0; i < 6; i++) sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
        catch
        {
            return "unknown";
        }
    }
}
