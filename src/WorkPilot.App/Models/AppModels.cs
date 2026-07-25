using System.Text.Json.Serialization;

namespace WorkPilot.Models;

public sealed record Space(string Id, string Name, string Description, string ColorToken,
    bool IsDefault, bool IsArchived, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, long RowVersion,
    int ProjectCount = 0, int TaskCount = 0)
{
    public string DisplayName => IsArchived ? $"{Name}（已归档）" : Name;
}

public sealed record Conversation(string Id, string SpaceId, string? ProjectId, string Title,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record ChatMessage(string Id, string ConversationId, string Role, string Content,
    DateTimeOffset CreatedAt, string? ToolName = null);

public sealed record Project(string Id, string SpaceId, string Name, string WorkspacePath,
    string Instructions, string IgnoreRules, bool IncludeHidden, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, long RowVersion)
{
    public string DisplayName => Name;
}

public sealed record WorkTask(string Id, string SpaceId, string? ProjectId, string? MainConversationId,
    string Title, string Description, string Status, string Priority, DateOnly? DueDate, long SortKey,
    DateTimeOffset? CompletedAt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, long RowVersion,
    string? ProjectName = null)
{
    public string StatusLabel => Status switch
    {
        "backlog" => "待规划", "todo" => "待处理", "in_progress" => "进行中",
        "blocked" => "已阻塞", "done" => "已完成", "cancelled" => "已取消", _ => Status
    };
    public string PriorityLabel => Priority switch
    {
        "low" => "低", "normal" => "普通", "high" => "高", "urgent" => "紧急", _ => Priority
    };
    public string DueLabel => DueDate is null ? "无截止日期" : DueDate.Value.ToString("yyyy-MM-dd");
}

public sealed record Automation(string Id, string Name, string Prompt, int IntervalMinutes, bool Enabled,
    DateTimeOffset? LastRunAt, DateTimeOffset NextRunAt, string LastStatus);

public sealed record AppSettings(string Endpoint, string Model, int PermissionMode, string? ActiveProjectId,
    string UserSystemPrompt, string? ActiveSpaceId, string TaskView, string? ActiveExpertId)
{
    public static AppSettings Default => new("https://api.openai.com/v1", "gpt-5-mini", 0, null,
        "回答应准确、可验证；修改项目文件前先说明计划，并保持现有代码风格。", null, "board", null);
}

public sealed record AgentEvent(string Kind, string Text, string? ToolName = null, string? ToolArguments = null);
public sealed record AgentRunOptions(string ConversationId, string UserText, Project? Project,
    AppSettings Settings, string? ExpertId = null, string? SpaceId = null);
public sealed record ModelToolDefinition(string Name, string Description, string ParametersJson);

public sealed class ModelMessage
{
    [JsonPropertyName("role")] public string Role { get; init; } = "user";
    [JsonPropertyName("content")] public object? Content { get; init; }
    [JsonPropertyName("tool_call_id")] public string? ToolCallId { get; init; }
    [JsonPropertyName("tool_calls")] public IReadOnlyList<ModelToolCall>? ToolCalls { get; init; }
}

public sealed class ModelToolCall
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "function";
    [JsonPropertyName("function")] public ModelFunction Function { get; set; } = new();
}

public sealed class ModelFunction
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("arguments")] public string Arguments { get; set; } = "{}";
}

public sealed record ModelTurn(string Text, IReadOnlyList<ModelToolCall> ToolCalls);
