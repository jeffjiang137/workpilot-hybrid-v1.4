using Microsoft.Data.Sqlite;
using WorkPilot.Models;

namespace WorkPilot.Services;

public sealed class TaskService(DatabaseService database)
{
    public async Task<IReadOnlyList<WorkTask>> QueryAsync(string spaceId, string? search = null,
        CancellationToken cancellationToken = default)
    {
        var result = new List<WorkTask>();
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.id,t.space_id,t.project_id,t.main_conversation_id,t.title,t.description,t.status,t.priority,
              t.due_date,t.sort_key,t.completed_at,t.created_at,t.updated_at,t.row_version,p.name
            FROM tasks t LEFT JOIN projects p ON p.id=t.project_id
            WHERE t.space_id=$space AND ($search='' OR t.title LIKE $pattern ESCAPE '\')
            ORDER BY t.status,t.sort_key,t.updated_at DESC LIMIT 2000
            """;
        command.Parameters.AddWithValue("$space", spaceId); command.Parameters.AddWithValue("$search", search?.Trim() ?? "");
        command.Parameters.AddWithValue("$pattern", "%" + EscapeLike(search?.Trim() ?? "") + "%");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(Map(reader));
        return result;
    }

    public async Task<WorkTask> SaveAsync(WorkTask item, CancellationToken cancellationToken = default)
    {
        var due = item.DueDate?.ToString("yyyy-MM-dd");
        TaskRules.Validate(item.Title, item.Description, item.Status, item.Priority, due);
        await ValidateProjectAsync(item.SpaceId, item.ProjectId, cancellationToken);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO tasks(id,space_id,project_id,main_conversation_id,title,description,status,priority,due_date,sort_key,completed_at,created_at,updated_at,row_version)
            VALUES($id,$space,$project,$conversation,$title,$description,$status,$priority,$due,$sort,$completed,$created,$updated,1)
            ON CONFLICT(id) DO UPDATE SET project_id=$project,main_conversation_id=$conversation,title=$title,description=$description,status=$status,
              priority=$priority,due_date=$due,sort_key=$sort,completed_at=$completed,updated_at=$updated,row_version=row_version+1
              WHERE tasks.row_version=$version AND tasks.space_id=$space
            """;
        Bind(command, item); var changed = await command.ExecuteNonQueryAsync(cancellationToken);
        if (changed == 0) throw new ConcurrencyConflict("任务", item.Id, await GetVersionAsync(item.Id, cancellationToken));
        return (await QueryAsync(item.SpaceId, cancellationToken: cancellationToken)).Single(x => x.Id == item.Id);
    }

    public async Task<WorkTask> ChangeStatusAsync(WorkTask item, string target,
        CancellationToken cancellationToken = default)
    {
        TaskRules.ValidateTransition(item.Status, target);
        var items = await QueryAsync(item.SpaceId, cancellationToken: cancellationToken);
        DateTimeOffset? completed = target == "done" ? DateTimeOffset.UtcNow : null;
        return await SaveAsync(item with { Status = target, SortKey = TaskRules.NextSortKey(items, target),
            CompletedAt = completed, UpdatedAt = DateTimeOffset.UtcNow }, cancellationToken);
    }

    public async Task<Conversation> EnsureConversationAsync(WorkTask item,
        CancellationToken cancellationToken = default)
    {
        if (item.MainConversationId is not null)
        {
            var existing = (await database.GetConversationsAsync(item.SpaceId, cancellationToken))
                .FirstOrDefault(x => x.Id == item.MainConversationId);
            if (existing is not null) return existing;
        }
        var conversation = await database.EnsureConversationAsync(item.SpaceId, item.ProjectId,
            cancellationToken: cancellationToken);
        var updated = item with { MainConversationId = conversation.Id, UpdatedAt = DateTimeOffset.UtcNow };
        await SaveAsync(updated, cancellationToken);
        return conversation;
    }

    public async Task DeleteAsync(WorkTask item, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM tasks WHERE id=$id AND row_version=$version";
        command.Parameters.AddWithValue("$id", item.Id); command.Parameters.AddWithValue("$version", item.RowVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0) throw new ConcurrencyConflict("任务", item.Id, item.RowVersion + 1);
    }

    private async Task ValidateProjectAsync(string spaceId, string? projectId, CancellationToken cancellationToken)
    {
        if (projectId is null) return;
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM projects WHERE id=$id AND space_id=$space";
        command.Parameters.AddWithValue("$id", projectId); command.Parameters.AddWithValue("$space", spaceId);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) != 1)
            throw new ValidationError("project", "cross_space", "项目不属于当前空间");
    }

    private async Task<long> GetVersionAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand(); command.CommandText = "SELECT row_version FROM tasks WHERE id=$id";
        command.Parameters.AddWithValue("$id", id); return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
    }

    private static void Bind(SqliteCommand command, WorkTask item)
    {
        command.Parameters.AddWithValue("$id", item.Id); command.Parameters.AddWithValue("$space", item.SpaceId);
        command.Parameters.AddWithValue("$project", (object?)item.ProjectId ?? DBNull.Value); command.Parameters.AddWithValue("$conversation", (object?)item.MainConversationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$title", item.Title.Trim()); command.Parameters.AddWithValue("$description", item.Description);
        command.Parameters.AddWithValue("$status", item.Status); command.Parameters.AddWithValue("$priority", item.Priority);
        command.Parameters.AddWithValue("$due", (object?)item.DueDate?.ToString("yyyy-MM-dd") ?? DBNull.Value); command.Parameters.AddWithValue("$sort", item.SortKey);
        command.Parameters.AddWithValue("$completed", (object?)item.CompletedAt?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", item.CreatedAt.ToString("O")); command.Parameters.AddWithValue("$updated", item.UpdatedAt.ToString("O")); command.Parameters.AddWithValue("$version", item.RowVersion);
    }

    private static WorkTask Map(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
        r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7),
        r.IsDBNull(8) ? null : DateOnly.ParseExact(r.GetString(8), "yyyy-MM-dd"), r.GetInt64(9),
        r.IsDBNull(10) ? null : DateTimeOffset.Parse(r.GetString(10)), DateTimeOffset.Parse(r.GetString(11)),
        DateTimeOffset.Parse(r.GetString(12)), r.GetInt64(13), r.IsDBNull(14) ? null : r.GetString(14));
    private static string EscapeLike(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
