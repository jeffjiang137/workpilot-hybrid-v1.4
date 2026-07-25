using Microsoft.Data.Sqlite;
using WorkPilot.Infrastructure.Data;
using WorkPilot.Models;

namespace WorkPilot.Services;

public sealed class DatabaseService
{
    private readonly string _connectionString;
    public string DatabasePath { get; }

    public DatabaseService(string? databasePath = null)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkPilot");
        Directory.CreateDirectory(directory);
        DatabasePath = databasePath ?? Path.Combine(directory, "workpilot.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await new DatabaseMigrator(this).InitializeAsync(cancellationToken);   // v12 -> 13
        await new V14DatabaseMigrator(this).InitializeAsync(cancellationToken); // v14/15/16

        // T23: schema version handshake for migrations 017-022. Forward-migrates when the database is
        // older, and refuses to start when the schema is newer or corrupt (MIG-A06/A07, PKG-A05/A06).
        // Without this the v17-22 tables would never be created and the App could not open its stores.
        await using (var connection = await OpenConnectionAsync(cancellationToken))
        {
            var handshake = new SchemaUpgradeHandshake(
                V15DatabaseMigrator.LatestVersion,
                V15DatabaseMigrator.LatestVersion,
                new SqliteSchemaVersionProbe(),
                new V15DatabaseMigrator(clock: null));
            var result = await handshake.PerformAsync(connection, isHost: false, cancellationToken);
            if (!result.Success)
            {
                throw new InvalidOperationException(
                    $"数据库架构不兼容，启动已停止（{result.Compatibility.Kind}: {result.Compatibility.MessageKey}）。请升级 WorkPilot 或恢复备份。");
            }
        }
    }

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    public async Task EnsureSafeIndexRuntimeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand(); command.CommandText = "SELECT sqlite_version()";
        var text = (await command.ExecuteScalarAsync(cancellationToken))?.ToString() ?? "0.0.0";
        if (!Version.TryParse(text, out var version) ||
            (version < new Version(3, 51, 3) && version != new Version(3, 50, 7) && version != new Version(3, 44, 6)))
            throw new InvalidOperationException($"SQLite {text} 不满足并发索引安全版本要求，请重新构建完整 V1.4 安装包");
    }

    public async Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        var values = new Dictionary<string, string>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT key,value FROM settings";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) values[reader.GetString(0)] = reader.GetString(1);
        var defaults = AppSettings.Default;
        return new(values.GetValueOrDefault("endpoint", defaults.Endpoint),
            values.GetValueOrDefault("model", defaults.Model),
            int.TryParse(values.GetValueOrDefault("permission_mode"), out var mode) ? mode : defaults.PermissionMode,
            values.GetValueOrDefault("active_project_id"),
            values.GetValueOrDefault("user_system_prompt", defaults.UserSystemPrompt),
            values.GetValueOrDefault("active_space_id"),
            values.GetValueOrDefault("task_view", defaults.TaskView),
            values.GetValueOrDefault("active_expert_id"));
    }

    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var values = new Dictionary<string, string?>
        {
            ["endpoint"] = settings.Endpoint, ["model"] = settings.Model,
            ["permission_mode"] = settings.PermissionMode.ToString(), ["active_project_id"] = settings.ActiveProjectId,
            ["user_system_prompt"] = settings.UserSystemPrompt, ["active_space_id"] = settings.ActiveSpaceId,
            ["task_view"] = settings.TaskView, ["active_expert_id"] = settings.ActiveExpertId
        };
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var pair in values)
        {
            var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = pair.Value is null
                ? "DELETE FROM settings WHERE key=$key"
                : "INSERT INTO settings(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=$value";
            command.Parameters.AddWithValue("$key", pair.Key);
            if (pair.Value is not null) command.Parameters.AddWithValue("$value", pair.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<Conversation> EnsureConversationAsync(string spaceId, string? projectId = null,
        string? id = null, CancellationToken cancellationToken = default)
    {
        if (id is not null)
        {
            var existing = (await GetConversationsAsync(spaceId, cancellationToken)).FirstOrDefault(x => x.Id == id);
            if (existing is not null) return existing;
        }
        var now = DateTimeOffset.UtcNow;
        var item = new Conversation(Guid.NewGuid().ToString("N"), spaceId, projectId, "新任务", now, now);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO conversations(id,space_id,project_id,title,created_at,updated_at) VALUES($id,$space,$project,$title,$created,$updated)";
        command.Parameters.AddWithValue("$id", item.Id); command.Parameters.AddWithValue("$space", spaceId);
        command.Parameters.AddWithValue("$project", (object?)projectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$title", item.Title); command.Parameters.AddWithValue("$created", now.ToString("O"));
        command.Parameters.AddWithValue("$updated", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return item;
    }

    public async Task<IReadOnlyList<Conversation>> GetConversationsAsync(string spaceId,
        CancellationToken cancellationToken = default)
    {
        var result = new List<Conversation>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT id,space_id,project_id,title,created_at,updated_at FROM conversations WHERE space_id=$space ORDER BY updated_at DESC LIMIT 100";
        command.Parameters.AddWithValue("$space", spaceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(new(reader.GetString(0), reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3),
            DateTimeOffset.Parse(reader.GetString(4)), DateTimeOffset.Parse(reader.GetString(5))));
        return result;
    }

    public async Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(string conversationId, int limit = 200,
        CancellationToken cancellationToken = default)
    {
        var result = new List<ChatMessage>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT id,conversation_id,role,content,created_at,tool_name FROM messages WHERE conversation_id=$id ORDER BY created_at DESC LIMIT $limit";
        command.Parameters.AddWithValue("$id", conversationId); command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(new(reader.GetString(0), reader.GetString(1),
            reader.GetString(2), reader.GetString(3), DateTimeOffset.Parse(reader.GetString(4)),
            reader.IsDBNull(5) ? null : reader.GetString(5)));
        result.Reverse();
        return result;
    }

    public async Task AddMessageAsync(ChatMessage message, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO messages(id,conversation_id,role,content,created_at,tool_name) VALUES($id,$cid,$role,$content,$created,$tool); UPDATE conversations SET updated_at=$created WHERE id=$cid";
        command.Parameters.AddWithValue("$id", message.Id); command.Parameters.AddWithValue("$cid", message.ConversationId);
        command.Parameters.AddWithValue("$role", message.Role); command.Parameters.AddWithValue("$content", message.Content);
        command.Parameters.AddWithValue("$created", message.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$tool", (object?)message.ToolName ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RenameConversationFromFirstMessageAsync(string id, string text,
        CancellationToken cancellationToken = default)
    {
        var title = text.Trim().ReplaceLineEndings(" ");
        if (title.Length > 28) title = title[..28] + "…";
        if (title.Length == 0) return;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE conversations SET title=$title WHERE id=$id AND title='新任务'";
        command.Parameters.AddWithValue("$title", title); command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
