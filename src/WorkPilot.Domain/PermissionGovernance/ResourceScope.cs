using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkPilot.Domain.PermissionGovernance;

/// <summary>
/// Strongly-typed resource scope as defined by doc 07 §4. The scope type is provided by the
/// capability definer; intersection rules are implemented per-same-type adapter in T17. Different
/// types intersect to empty (Deny). Serialized via <see cref="JsonSerializer"/> (typed, not the
/// JSON-DOM API) with a discriminated <c>kind</c> field for stable storage in
/// <c>policy_statements.resource_scope_json</c>.
/// </summary>
[JsonDerivedType(typeof(LocalProjectScope), "local_project")]
[JsonDerivedType(typeof(GitHubScope), "github")]
[JsonDerivedType(typeof(NotionScope), "notion")]
[JsonDerivedType(typeof(McpScope), "mcp")]
[JsonDerivedType(typeof(BuiltinScope), "builtin")]
public abstract record ResourceScope
{
    /// <summary>Stable discriminator. Never localized; part of the storage contract.</summary>
    [JsonPropertyName("kind")]
    public abstract string Kind { get; }

    public string ToStorageJson() => JsonSerializer.Serialize(this);

    public static ResourceScope FromStorageJson(string json)
    {
        var scope = JsonSerializer.Deserialize<ResourceScope>(json);
        if (scope is null)
            throw new InvalidDataException("资源范围 JSON 反序列化为空");
        return scope;
    }
}

/// <summary>Local project file scope. Paths are normalized to project root by the C++ Core before compare.</summary>
public sealed record LocalProjectScope(
    string ProjectId,
    IReadOnlyList<string> RelativeRoots,
    IReadOnlyList<string> Operations) : ResourceScope
{
    public override string Kind => "local_project";
}

/// <summary>GitHub repository scope (owner_hash/repo_hash).</summary>
public sealed record GitHubScope(
    string AccountId,
    IReadOnlyList<string> Repositories,
    IReadOnlyList<string> Operations) : ResourceScope
{
    public override string Kind => "github";
}

/// <summary>Notion pages/databases scope (id_hmac).</summary>
public sealed record NotionScope(
    string AccountId,
    IReadOnlyList<string> PagesOrDatabases,
    IReadOnlyList<string> Operations) : ResourceScope
{
    public override string Kind => "notion";
}

/// <summary>MCP server capability scope.</summary>
public sealed record McpScope(
    string ServerId,
    string CapabilityId,
    string SchemaSha256,
    string TargetConstraints) : ResourceScope
{
    public override string Kind => "mcp";
}

/// <summary>Built-in capability scope (entity id optional).</summary>
public sealed record BuiltinScope(
    string CapabilityId,
    IReadOnlyList<string>? EntityIds) : ResourceScope
{
    public override string Kind => "builtin";
}
