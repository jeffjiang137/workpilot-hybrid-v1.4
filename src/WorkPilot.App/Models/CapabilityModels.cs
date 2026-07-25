using System.Text.Json;

namespace WorkPilot.Models;

public enum RiskLevel { Low = 0, Medium = 1, High = 2, Critical = 3 }
public enum SourceKind { Builtin, Connector, Mcp }

public sealed record Expert(
    string Id, string Name, string Description, string ColorKey, string Status,
    string CurrentRevisionId, int RevisionNumber, string ModelPreference,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, long RowVersion,
    int SkillCount = 0, int ConnectionCount = 0)
{
    public string DisplayName => Status == "archived" ? $"{Name}（已归档）" : Name;
    public string RevisionLabel => $"修订 {RevisionNumber}";
}

public sealed record ExpertRevision(
    string Id, string ExpertId, int RevisionNumber, string ModelPreference,
    string SystemInstruction, string CapabilityPolicyJson, string SnapshotJson,
    string SnapshotSha256, DateTimeOffset CreatedAt);

public sealed record ExpertDraft(
    string Name, string Description, string ColorKey, string ModelPreference,
    string SystemInstruction, IReadOnlyList<string> SkillVersionIds,
    IReadOnlyList<string> ConnectorAccountIds, IReadOnlyList<string> McpServerIds,
    RiskLevel MaximumRisk = RiskLevel.High,
    IReadOnlyList<string>? AutomaticSkillVersionIds = null);

public sealed record Skill(
    string Id, string Publisher, string DisplayName, string Version, string Description,
    string Status, string PackageSha256, string ContentRoot, DateTimeOffset InstalledAt,
    int ExpertCount = 0)
{
    public string DisplayVersion => $"{Publisher} · {Version}";
}

public sealed record SkillManifest(
    int SchemaVersion, string Id, string Name, string Publisher, string Version,
    string Description, string Entrypoint, string MinWorkPilotVersion,
    SkillActivation? Activation, IReadOnlyList<string>? RequiredCapabilities);

public sealed record SkillActivation(IReadOnlyList<string>? Aliases, IReadOnlyList<string>? Tags);

public sealed record SkillInspection(
    string Token, string SourcePath, SkillManifest Manifest, string PackageSha256,
    int FileCount, long UncompressedBytes, IReadOnlyList<string> Files,
    IReadOnlyList<string> Warnings);

public sealed record SkillVersionChoice(string VersionId, Skill Skill)
{
    public string DisplayName => $"{Skill.DisplayName} · {Skill.Version}";
}

public sealed record SkillActivationEvidence(
    string SkillId, string Version, bool Pinned, double Score,
    IReadOnlyList<string> Matches, string? ExclusionReason = null);

public sealed record ConnectorAccount(
    string Id, string Kind, string DisplayName, string IdentitySummary,
    string CredentialRef, IReadOnlyList<string> GrantedScopes, string State,
    DateTimeOffset? LastSuccessAt, string? LastErrorCode, long RowVersion)
{
    public string StatusLabel => State switch
    {
        "connected" => "已连接", "degraded" => "服务异常", "expired" => "凭据已过期",
        "disabled" => "已禁用", "authenticating" => "正在验证", _ => "未连接"
    };
}

public sealed record ConnectorCapability(
    string StableId, string Kind, string Operation, string Title, string Description,
    RiskLevel Risk, bool Mutating, JsonElement InputSchema);

public sealed record McpServer(
    string Id, string DisplayName, string TransportKind, string ConfigJson,
    string? CredentialRef, bool Enabled, string State, string? NegotiatedProtocol,
    string CapabilityHash, DateTimeOffset? LastConnectedAt, string? LastErrorCode,
    long RowVersion)
{
    public string TransportLabel => TransportKind == "stdio" ? "本地 stdio" : "Streamable HTTP";
}

public sealed record McpServerDraft(
    string DisplayName, string TransportKind, string? Executable,
    IReadOnlyList<string> Arguments, string? WorkingDirectory,
    string? Endpoint, bool LocalMode, string? BearerToken);

public sealed record McpCapability(
    string Id, string ServerId, string Kind, string RemoteName, string StableName,
    string Title, string Description, string InputSchemaJson, string AnnotationsJson,
    RiskLevel LocalRisk, string SchemaSha256, string Status)
{
    public string ReviewLabel => $"[{Kind}] {Title} · 风险 {LocalRisk} · {RemoteName}";
}

public sealed record CapabilityAudit(
    long Id, string? RunSnapshotId, string? ExpertId, string? SpaceId,
    string SourceKind, string SourceId, string CapabilityStableId,
    RiskLevel Risk, string Decision, string Outcome, string? ErrorCategory,
    long DurationMs, long ResultSize, DateTimeOffset CreatedAt);

public sealed record AgentRunSnapshot(
    string Id, string ConversationId, string ExpertRevisionId, string SpaceId,
    string? ProjectId, string ModelId, IReadOnlyList<string> SkillVersionIds,
    IReadOnlyList<string> CapabilityIds, string SnapshotSha256, DateTimeOffset CreatedAt);

public sealed record CapabilityInvocation(
    string RunSnapshotId, string StableId, SourceKind SourceKind, string SourceId,
    JsonElement Arguments, RiskLevel Risk, bool Mutating, string SchemaSha256);

public sealed record CapabilityResult(
    bool Success, string Text, int ItemCount = 0, bool IsTruncated = false,
    string? ErrorCategory = null);

public sealed record RuntimeCapability(
    string ModelName, string StableId, SourceKind SourceKind, string SourceId,
    string SourceLabel, string Title, string Description, RiskLevel Risk,
    bool Mutating, string SchemaJson, string SchemaSha256, string InternalCapabilityId);
