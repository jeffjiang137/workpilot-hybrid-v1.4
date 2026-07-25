using System;
using System.Text;
using System.Text.Json;

namespace WorkPilot.Domain.PermissionGovernance.Evaluation;

/// <summary>
/// Source / capability selector used by a <see cref="PolicyStatement"/>. The selector is stored as
/// JSON on the statement (<c>source_selector</c> / <c>capability_selector</c>); this type parses and
/// matches it. A <c>match:"all"</c> selector (used only for Deny rules) matches every id; an
/// <c>match:"id"</c> selector matches a single stable id and optionally pins a schema hash.
/// Matching is exact-stable-id only — there are no wildcards for Allow (doc 07 §3), and selectors
/// never perform prefix/substring matching.
/// </summary>
public sealed record Selector(bool MatchAll, string? StableId, string? SchemaSha256)
{
    /// <summary>Selector that matches any source/capability (Deny floors only).</summary>
    public const string MatchAllJson = "{\"match\":\"all\"}";

    public static Selector All() => new(true, null, null);

    public static Selector ForId(string stableId, string? schemaSha256 = null)
        => new(false, stableId, schemaSha256);

    /// <summary>Serializes this selector to its canonical stored JSON form.</summary>
    public string ToJson()
    {
        if (MatchAll)
            return MatchAllJson;
        var stableId = (StableId ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        var b = new StringBuilder("{\"match\":\"id\",\"id\":\"").Append(stableId).Append('\"');
        if (SchemaSha256 is not null)
            b.Append(",\"schema_sha256\":\"").Append(SchemaSha256).Append('\"');
        b.Append('}');
        return b.ToString();
    }

    public static Selector Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return All();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("match", out var m) && m.ValueKind == JsonValueKind.String && m.GetString() == "all")
                return All();
            var id = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
            var schema = root.TryGetProperty("schema_sha256", out var sEl) && sEl.ValueKind == JsonValueKind.String ? sEl.GetString() : null;
            return new Selector(false, id, schema);
        }
        catch (JsonException)
        {
            // Malformed selector → fail closed to "match nothing" (never silently allow).
            return new Selector(false, null, null);
        }
    }

    /// <summary>True if this selector permits the given stable id (and optional current schema hash).</summary>
    public bool Matches(string stableId, string? currentSchemaSha256)
    {
        if (MatchAll)
            return true;
        if (StableId is null || !string.Equals(StableId, stableId, StringComparison.Ordinal))
            return false;
        if (SchemaSha256 is not null && currentSchemaSha256 is not null
            && !string.Equals(SchemaSha256, currentSchemaSha256, StringComparison.Ordinal))
            return false;
        return true;
    }
}
