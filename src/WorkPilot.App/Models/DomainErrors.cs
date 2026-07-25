namespace WorkPilot.Models;

public sealed class ValidationError(string field, string code, string message) : Exception(message)
{
    public string Field { get; } = field;
    public string Code { get; } = code;
}

public sealed class ConcurrencyConflict(string entity, string id, long currentVersion)
    : Exception($"{entity} 已被其他操作更新，请刷新后重试")
{
    public string Entity { get; } = entity;
    public string Id { get; } = id;
    public long CurrentVersion { get; } = currentVersion;
}

public sealed class IndexUnavailableError(string projectId, string state)
    : Exception($"项目索引当前不可用（{state}），请先在项目页完成索引")
{
    public string ProjectId { get; } = projectId;
    public string State { get; } = state;
}
