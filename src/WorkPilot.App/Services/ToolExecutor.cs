using System.Text.Json;
using WorkPilot.Models;

namespace WorkPilot.Services;

public sealed record ToolPlan(string Name, string Arguments, int Risk, bool Mutating);

public sealed class ToolExecutor(INativeWorkspaceSession workspace, AssetSearchService assetSearch, Project project)
{
    public ToolPlan Validate(string name, string arguments)
    {
        if (arguments.Length > 1_100_000) throw new ArgumentException("工具参数过大");
        using var document = JsonDocument.Parse(arguments);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("工具参数必须是 JSON 对象");
        RejectUnknown(document.RootElement, name switch
        {
            "list_files" => ["path"], "read_text_file" => ["path"],
            "write_text_file" => ["path", "content", "expected_sha256"],
            "search_assets" => ["query", "max_results"], _ => []
        });
        return name switch
        {
            "list_files" => new(name, arguments, 0, false),
            "read_text_file" => RequirePath(name, arguments, document.RootElement, 0, false),
            "write_text_file" => ValidateWrite(name, arguments, document.RootElement),
            "search_assets" => ValidateSearch(name, arguments, document.RootElement),
            _ => throw new ArgumentException($"未知工具：{name}")
        };
    }

    public async Task<string> ExecuteAsync(ToolPlan plan, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(plan.Arguments); var root = document.RootElement;
        return plan.Name switch
        {
            "list_files" => workspace.ListFiles(GetOptionalString(root, "path") ?? ""),
            "read_text_file" => workspace.ReadText(GetRequiredString(root, "path")),
            "write_text_file" => workspace.WriteText(GetRequiredString(root, "path"),
                GetRequiredString(root, "content"), GetOptionalString(root, "expected_sha256")),
            "search_assets" => await assetSearch.SearchForAgentAsync(project, GetRequiredString(root, "query"),
                root.TryGetProperty("max_results", out var max) ? max.GetInt32() : 8, cancellationToken),
            _ => throw new ArgumentException($"未知工具：{plan.Name}")
        };
    }

    private static ToolPlan RequirePath(string name, string arguments, JsonElement root, int risk, bool mutating)
    {
        ValidatePath(GetRequiredString(root, "path")); return new(name, arguments, risk, mutating);
    }

    private static ToolPlan ValidateWrite(string name, string arguments, JsonElement root)
    {
        ValidatePath(GetRequiredString(root, "path")); var content = GetRequiredString(root, "content");
        if (content.Contains('\0')) throw new ArgumentException("文本内容不能包含 NUL 字符");
        if (System.Text.Encoding.UTF8.GetByteCount(content) > 1024 * 1024) throw new ArgumentException("写入内容超过 1 MiB");
        var hash = GetOptionalString(root, "expected_sha256");
        if (hash is not null && (hash.Length != 64 || hash.Any(value => !Uri.IsHexDigit(value))))
            throw new ArgumentException("expected_sha256 必须是 64 位十六进制字符串");
        return new(name, arguments, 2, true);
    }

    private static ToolPlan ValidateSearch(string name, string arguments, JsonElement root)
    {
        var query = GetRequiredString(root, "query").Trim();
        if (query.Length is < 1 or > 200) throw new ArgumentException("搜索词需为 1–200 个字符");
        if (root.TryGetProperty("max_results", out var max) &&
            (max.ValueKind != JsonValueKind.Number || !max.TryGetInt32(out var number) || number is < 1 or > 8))
            throw new ArgumentException("max_results 需为 1–8 的整数");
        return new(name, arguments, 0, false);
    }

    private static void RejectUnknown(JsonElement root, IReadOnlyCollection<string> allowed)
    {
        foreach (var property in root.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal)) throw new ArgumentException($"不支持参数：{property.Name}");
    }

    private static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 500 || path.Contains('\0')) throw new ArgumentException("文件路径为空、过长或含 NUL 字符");
        if (Path.IsPathRooted(path)) throw new ArgumentException("只允许项目相对路径");
    }

    private static string GetRequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"缺少字符串参数：{name}");
        return value.GetString()!;
    }
    private static string? GetOptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
