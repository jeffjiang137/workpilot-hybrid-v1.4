using System.Text.Json;
using WorkPilot.Models;

namespace WorkPilot.Services;

public static class ConnectorRegistry
{
    public static IReadOnlyList<ConnectorCapability> Get(string kind) => kind switch
    {
        "github" => GitHub,
        "notion" => Notion,
        _ => []
    };

    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static readonly IReadOnlyList<ConnectorCapability> GitHub =
    [
        Cap("github.search_repositories", "github", "search_repositories", "搜索仓库", "按关键字搜索可见 GitHub 仓库", RiskLevel.Medium, false,
            "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\",\"maxLength\":200}},\"required\":[\"query\"],\"additionalProperties\":false}"),
        Cap("github.get_repository", "github", "get_repository", "读取仓库", "读取指定仓库元数据", RiskLevel.Medium, false, OwnerRepo),
        Cap("github.list_issues", "github", "list_issues", "列出 Issue", "列出指定仓库的 Issue", RiskLevel.Medium, false, OwnerRepo),
        Cap("github.get_issue", "github", "get_issue", "读取 Issue", "读取指定 Issue", RiskLevel.Medium, false, OwnerRepoNumber),
        Cap("github.list_pull_requests", "github", "list_pull_requests", "列出 PR", "列出指定仓库的 Pull Request", RiskLevel.Medium, false, OwnerRepo),
        Cap("github.get_pull_request", "github", "get_pull_request", "读取 PR", "读取指定 Pull Request", RiskLevel.Medium, false, OwnerRepoNumber),
        Cap("github.get_file_text", "github", "get_file_text", "读取仓库文件", "读取不超过 1 MiB 的文本文件", RiskLevel.Medium, false,
            "{\"type\":\"object\",\"properties\":{\"owner\":{\"type\":\"string\"},\"repo\":{\"type\":\"string\"},\"path\":{\"type\":\"string\"},\"ref\":{\"type\":\"string\"}},\"required\":[\"owner\",\"repo\",\"path\"],\"additionalProperties\":false}"),
        Cap("github.create_issue", "github", "create_issue", "创建 Issue", "在指定仓库创建 Issue", RiskLevel.High, true,
            "{\"type\":\"object\",\"properties\":{\"owner\":{\"type\":\"string\"},\"repo\":{\"type\":\"string\"},\"title\":{\"type\":\"string\",\"maxLength\":200},\"body\":{\"type\":\"string\",\"maxLength\":20000}},\"required\":[\"owner\",\"repo\",\"title\"],\"additionalProperties\":false}"),
        Cap("github.add_issue_comment", "github", "add_issue_comment", "添加 Issue 评论", "向 Issue 或 PR 添加评论", RiskLevel.High, true,
            "{\"type\":\"object\",\"properties\":{\"owner\":{\"type\":\"string\"},\"repo\":{\"type\":\"string\"},\"number\":{\"type\":\"integer\"},\"body\":{\"type\":\"string\",\"maxLength\":20000}},\"required\":[\"owner\",\"repo\",\"number\",\"body\"],\"additionalProperties\":false}")
    ];

    private static readonly IReadOnlyList<ConnectorCapability> Notion =
    [
        Cap("notion.search", "notion", "search", "搜索 Notion", "搜索集成可访问的页面与数据库", RiskLevel.Medium, false,
            "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\",\"maxLength\":200}},\"required\":[\"query\"],\"additionalProperties\":false}"),
        Cap("notion.get_page", "notion", "get_page", "读取页面", "读取 Notion 页面属性和内容", RiskLevel.Medium, false, IdSchema("page_id")),
        Cap("notion.get_database", "notion", "get_database", "读取数据库", "读取 Notion 数据库定义", RiskLevel.Medium, false, IdSchema("database_id")),
        Cap("notion.query_database", "notion", "query_database", "查询数据库", "查询 Notion 数据库前 100 条", RiskLevel.Medium, false, IdSchema("database_id")),
        Cap("notion.append_page_blocks", "notion", "append_page_blocks", "追加页面内容", "向页面追加受支持的文本块", RiskLevel.High, true,
            "{\"type\":\"object\",\"properties\":{\"page_id\":{\"type\":\"string\"},\"text\":{\"type\":\"string\",\"maxLength\":20000}},\"required\":[\"page_id\",\"text\"],\"additionalProperties\":false}")
    ];

    private const string OwnerRepo = "{\"type\":\"object\",\"properties\":{\"owner\":{\"type\":\"string\"},\"repo\":{\"type\":\"string\"}},\"required\":[\"owner\",\"repo\"],\"additionalProperties\":false}";
    private const string OwnerRepoNumber = "{\"type\":\"object\",\"properties\":{\"owner\":{\"type\":\"string\"},\"repo\":{\"type\":\"string\"},\"number\":{\"type\":\"integer\"}},\"required\":[\"owner\",\"repo\",\"number\"],\"additionalProperties\":false}";
    private static string IdSchema(string name) => $"{{\"type\":\"object\",\"properties\":{{\"{name}\":{{\"type\":\"string\"}}}},\"required\":[\"{name}\"],\"additionalProperties\":false}}";
    private static ConnectorCapability Cap(string id, string kind, string operation, string title,
        string description, RiskLevel risk, bool mutating, string schema) =>
        new(id, kind, operation, title, description, risk, mutating, Schema(schema));
}
