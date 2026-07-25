using Microsoft.Data.Sqlite;
using WorkPilot.Models;

namespace WorkPilot.Services;

public sealed class ProjectRepository(DatabaseService database)
{
    public async Task<IReadOnlyList<Project>> GetBySpaceAsync(string spaceId,
        CancellationToken cancellationToken = default)
    {
        var result = new List<Project>();
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT id,space_id,name,workspace_path,instructions,ignore_rules,include_hidden,created_at,updated_at,row_version FROM projects WHERE space_id=$space ORDER BY updated_at DESC";
        command.Parameters.AddWithValue("$space", spaceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(Map(reader));
        return result;
    }

    public async Task<Project?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT id,space_id,name,workspace_path,instructions,ignore_rules,include_hidden,created_at,updated_at,row_version FROM projects WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Map(reader) : null;
    }

    public async Task<Project> SaveAsync(Project project, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO projects(id,space_id,name,workspace_path,instructions,ignore_rules,include_hidden,created_at,updated_at,row_version)
            VALUES($id,$space,$name,$path,$instructions,$rules,$hidden,$created,$updated,1)
            ON CONFLICT(id) DO UPDATE SET name=$name,workspace_path=$path,instructions=$instructions,
              ignore_rules=$rules,include_hidden=$hidden,updated_at=$updated,row_version=row_version+1
              WHERE projects.row_version=$version AND projects.space_id=$space
            """;
        Bind(command, project);
        var changed = await command.ExecuteNonQueryAsync(cancellationToken);
        if (changed == 0) throw new ConcurrencyConflict("项目", project.Id, await GetVersionAsync(project.Id, cancellationToken));
        return (await GetAsync(project.Id, cancellationToken))!;
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand(); command.CommandText = "DELETE FROM projects WHERE id=$id";
        command.Parameters.AddWithValue("$id", id); await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<long> GetVersionAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand(); command.CommandText = "SELECT row_version FROM projects WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
    }

    private static void Bind(SqliteCommand command, Project item)
    {
        command.Parameters.AddWithValue("$id", item.Id); command.Parameters.AddWithValue("$space", item.SpaceId);
        command.Parameters.AddWithValue("$name", item.Name); command.Parameters.AddWithValue("$path", item.WorkspacePath);
        command.Parameters.AddWithValue("$instructions", item.Instructions); command.Parameters.AddWithValue("$rules", item.IgnoreRules);
        command.Parameters.AddWithValue("$hidden", item.IncludeHidden); command.Parameters.AddWithValue("$created", item.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated", item.UpdatedAt.ToString("O")); command.Parameters.AddWithValue("$version", item.RowVersion);
    }

    private static Project Map(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1),
        reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetBoolean(6),
        DateTimeOffset.Parse(reader.GetString(7)), DateTimeOffset.Parse(reader.GetString(8)), reader.GetInt64(9));
}
