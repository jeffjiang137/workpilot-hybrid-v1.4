using System.Globalization;
using WorkPilot.Models;

namespace WorkPilot.Services;

public static class TaskRules
{
    public static readonly string[] Statuses = ["backlog", "todo", "in_progress", "blocked", "done", "cancelled"];
    public static readonly string[] Priorities = ["low", "normal", "high", "urgent"];
    private static readonly IReadOnlyDictionary<string, HashSet<string>> Transitions =
        new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["backlog"] = ["todo", "in_progress", "done", "cancelled"],
            ["todo"] = ["backlog", "in_progress", "done", "cancelled"],
            ["in_progress"] = ["todo", "blocked", "done", "cancelled"],
            ["blocked"] = ["todo", "in_progress", "done", "cancelled"],
            ["done"] = ["todo"], ["cancelled"] = ["todo"]
        };

    public static void Validate(string title, string description, string status, string priority, string? dueDate)
    {
        var titleLength = new StringInfo(title.Trim()).LengthInTextElements;
        if (titleLength is < 1 or > 120) throw new ValidationError("title", "length", "任务标题需为 1–120 个字符");
        if (new StringInfo(description).LengthInTextElements > 10_000) throw new ValidationError("description", "length", "任务描述最多 10,000 个字符");
        if (!Statuses.Contains(status, StringComparer.Ordinal)) throw new ValidationError("status", "invalid", "任务状态无效");
        if (!Priorities.Contains(priority, StringComparer.Ordinal)) throw new ValidationError("priority", "invalid", "任务优先级无效");
        if (dueDate is not null && !DateOnly.TryParseExact(dueDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _)) throw new ValidationError("due_date", "invalid", "截止日期必须是有效的 YYYY-MM-DD 日期");
    }

    public static void ValidateTransition(string from, string to)
    {
        if (from == to) return;
        if (!Transitions.TryGetValue(from, out var allowed) || !allowed.Contains(to))
            throw new ValidationError("status", "transition", $"不能从 {from} 变更为 {to}");
    }

    public static long NextSortKey(IEnumerable<WorkTask> items, string status)
    {
        var last = items.Where(x => x.Status == status).Select(x => x.SortKey).DefaultIfEmpty(0).Max();
        return last > long.MaxValue - 1024 ? throw new OverflowException("任务排序空间已用尽，请重新载入") : last + 1024;
    }
}
