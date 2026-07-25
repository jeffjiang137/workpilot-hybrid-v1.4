using System.Globalization;
using Microsoft.Data.Sqlite;
using WorkPilot.Models;

namespace WorkPilot.Services;

public sealed class SpaceService(DatabaseService database)
{
    private static readonly HashSet<string> Colors = new(StringComparer.Ordinal)
        { "green", "blue", "cyan", "violet", "amber", "orange", "rose", "slate" };
    public event EventHandler<Space>? ActiveSpaceChanged;

    public async Task<IReadOnlyList<Space>> ListAsync(bool includeArchived = false,
        CancellationToken cancellationToken = default)
    {
        var items = new List<Space>();
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.id,s.name,s.description,s.color_token,s.is_default,s.is_archived,s.created_at,s.updated_at,s.row_version,
              (SELECT COUNT(*) FROM projects p WHERE p.space_id=s.id),
              (SELECT COUNT(*) FROM tasks t WHERE t.space_id=s.id)
            FROM spaces s WHERE $archived=1 OR s.is_archived=0 ORDER BY s.is_default DESC,s.updated_at DESC
            """;
        command.Parameters.AddWithValue("$archived", includeArchived);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) items.Add(Map(reader));
        return items;
    }

    public async Task<Space> EnsureActiveAsync(string? requestedId, CancellationToken cancellationToken = default)
    {
        var items = await ListAsync(false, cancellationToken);
        var active = items.FirstOrDefault(x => x.Id == requestedId) ?? items.OrderBy(x => x.CreatedAt).First();
        if (active.Id != requestedId) await SetActiveSettingAsync(active.Id, cancellationToken);
        return active;
    }

    public async Task<Space> CreateAsync(string name, string description, string color,
        CancellationToken cancellationToken = default)
    {
        Validate(name, description, color);
        var now = DateTimeOffset.UtcNow; var id = Guid.NewGuid().ToString("N");
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO spaces(id,name,description,color_token,is_default,is_archived,created_at,updated_at,row_version) VALUES($id,$name,$description,$color,0,0,$now,$now,1)";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$name", name.Trim());
        command.Parameters.AddWithValue("$description", description.Trim()); command.Parameters.AddWithValue("$color", color);
        command.Parameters.AddWithValue("$now", now.ToString("O")); await command.ExecuteNonQueryAsync(cancellationToken);
        var item = (await ListAsync(true, cancellationToken)).Single(x => x.Id == id);
        await SetActiveAsync(item, cancellationToken); return item;
    }

    public async Task<Space> UpdateAsync(Space item, string name, string description, string color,
        CancellationToken cancellationToken = default)
    {
        Validate(name, description, color);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE spaces SET name=$name,description=$description,color_token=$color,updated_at=$now,row_version=row_version+1 WHERE id=$id AND row_version=$version";
        command.Parameters.AddWithValue("$name", name.Trim()); command.Parameters.AddWithValue("$description", description.Trim());
        command.Parameters.AddWithValue("$color", color); command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", item.Id); command.Parameters.AddWithValue("$version", item.RowVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0) throw new ConcurrencyConflict("空间", item.Id, item.RowVersion + 1);
        return (await ListAsync(true, cancellationToken)).Single(x => x.Id == item.Id);
    }

    public async Task ArchiveAsync(Space item, bool archived, CancellationToken cancellationToken = default)
    {
        if (item.IsDefault && archived) throw new ValidationError("space", "default_archive", "默认空间不能归档");
        await UpdateArchiveAsync(item, archived, cancellationToken);
    }

    public async Task DeleteEmptyAsync(Space item, CancellationToken cancellationToken = default)
    {
        if (item.IsDefault) throw new ValidationError("space", "default_delete", "默认空间不能删除");
        var refreshed = (await ListAsync(true, cancellationToken)).Single(x => x.Id == item.Id);
        if (refreshed.ProjectCount > 0 || refreshed.TaskCount > 0)
            throw new ValidationError("space", "not_empty", $"空间仍有 {refreshed.ProjectCount} 个项目和 {refreshed.TaskCount} 个任务，请先处理后再删除");
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand(); command.CommandText = "DELETE FROM spaces WHERE id=$id AND row_version=$version";
        command.Parameters.AddWithValue("$id", item.Id); command.Parameters.AddWithValue("$version", refreshed.RowVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0) throw new ConcurrencyConflict("空间", item.Id, refreshed.RowVersion + 1);
    }

    public async Task SetActiveAsync(Space item, CancellationToken cancellationToken = default)
    {
        if (item.IsArchived) throw new ValidationError("space", "archived", "请先恢复已归档空间");
        await SetActiveSettingAsync(item.Id, cancellationToken); ActiveSpaceChanged?.Invoke(this, item);
    }

    private async Task UpdateArchiveAsync(Space item, bool value, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE spaces SET is_archived=$value,updated_at=$now,row_version=row_version+1 WHERE id=$id AND row_version=$version";
        command.Parameters.AddWithValue("$value", value); command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", item.Id); command.Parameters.AddWithValue("$version", item.RowVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0) throw new ConcurrencyConflict("空间", item.Id, item.RowVersion + 1);
    }

    private async Task SetActiveSettingAsync(string id, CancellationToken cancellationToken)
    {
        var settings = await database.LoadSettingsAsync(cancellationToken);
        await database.SaveSettingsAsync(settings with { ActiveSpaceId = id, ActiveProjectId = null }, cancellationToken);
    }

    private static void Validate(string name, string description, string color)
    {
        var nameLength = new StringInfo(name.Trim()).LengthInTextElements;
        if (nameLength is < 1 or > 40) throw new ValidationError("name", "length", "空间名称需为 1–40 个字符");
        if (new StringInfo(description.Trim()).LengthInTextElements > 500) throw new ValidationError("description", "length", "空间描述最多 500 个字符");
        if (!Colors.Contains(color)) throw new ValidationError("color", "invalid", "请选择预设空间颜色");
    }

    private static Space Map(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
        reader.GetString(3), reader.GetBoolean(4), reader.GetBoolean(5), DateTimeOffset.Parse(reader.GetString(6)),
        DateTimeOffset.Parse(reader.GetString(7)), reader.GetInt64(8), reader.GetInt32(9), reader.GetInt32(10));
}
