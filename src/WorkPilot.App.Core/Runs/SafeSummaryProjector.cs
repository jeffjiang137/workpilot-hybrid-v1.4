using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WorkPilot.Domain.Automation.Run;

namespace WorkPilot.App.Core.Runs;

/// <summary>
/// Projects run I/O into a safe summary (LOG-004): only field names, byte sizes, and a hash alias for
/// target/recipient fields. The original values are never retained, logged, or returned — only a
/// truncated SHA-256 alias of target fields is kept for display correlation. No body, no secret.
/// </summary>
public static class SafeSummaryProjector
{
    /// <summary>Names that identify a target/recipient; their value is replaced by a hash alias.</summary>
    private static readonly HashSet<string> TargetMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "target", "url", "uri", "endpoint", "recipient", "destination", "email", "mail", "token", "webhook"
    };

    /// <summary>Projects input and output JSON bags into a safe summary. Null/empty input yields empty fields.</summary>
    public static SafeSummary Project(string? inputJson, string? outputJson)
    {
        var inputs = ProjectMembers(inputJson);
        var outputs = ProjectMembers(outputJson);
        var inBytes = 0;
        var outBytes = 0;
        foreach (var f in inputs) inBytes += f.ByteSize;
        foreach (var f in outputs) outBytes += f.ByteSize;
        return new SafeSummary(inputs, outputs, inBytes, outBytes);
    }

    private static IReadOnlyList<SafeFieldSummary> ProjectMembers(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<SafeFieldSummary>();

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return Array.Empty<SafeFieldSummary>(); }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return Array.Empty<SafeFieldSummary>();

            var result = new List<SafeFieldSummary>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var byteSize = prop.Value.ValueKind == JsonValueKind.String
                    ? Encoding.UTF8.GetByteCount(prop.Value.GetString()!)
                    : Encoding.UTF8.GetByteCount(prop.Value.GetRawText());
                var alias = IsTargetName(prop.Name)
                    ? HashAlias(prop.Name + "\u0000" + prop.Value.GetRawText())
                    : null;
                result.Add(new SafeFieldSummary(prop.Name, byteSize, alias));
            }
            return result;
        }
    }

    private static bool IsTargetName(string name)
    {
        var lower = name.ToLowerInvariant();
        foreach (var marker in TargetMarkers)
            if (lower.Contains(marker))
                return true;
        return false;
    }

    /// <summary>A truncated SHA-256 alias of the target value — never the value itself.</summary>
    private static string HashAlias(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToBase64String(hash)[..16];
    }
}
