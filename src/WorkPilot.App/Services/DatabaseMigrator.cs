using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace WorkPilot.Services;

public sealed class DatabaseMigrator(DatabaseService database)
{
    private static readonly string[] BaselineTables = ["settings", "conversations", "messages", "projects", "automations"];
    private const string MigrationName = "013_spaces_tasks_assets";

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var existed = File.Exists(database.DatabasePath) && new FileInfo(database.DatabasePath).Length > 0;
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var tableCount = await CountBaselineTablesAsync(connection, cancellationToken);
        if (tableCount is > 0 and < 5)
            throw new InvalidDataException("检测到不完整的 V1.2 数据库结构，迁移已停止；请使用迁移前备份恢复");
        await EnsureBaselineAsync(connection, cancellationToken);
        if (await HasMigrationAsync(connection, 13, cancellationToken)) return;
        await VerifyBaselineAsync(connection, cancellationToken);
        var backupPath = existed ? CreateBackup(connection) : null;
        await ApplyV13Async(connection, cancellationToken);
        try { await VerifyIntegrityAsync(connection, cancellationToken); }
        catch
        {
            await connection.CloseAsync();
            if (backupPath is not null) RestoreBackup(backupPath);
            throw;
        }
    }

    private static async Task<int> CountBaselineTablesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var count = 0;
        foreach (var table in BaselineTables)
        {
            var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name";
            command.Parameters.AddWithValue("$name", table);
            count += Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        }
        return count;
    }

    private static async Task EnsureBaselineAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY,value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS conversations(id TEXT PRIMARY KEY,title TEXT NOT NULL,created_at TEXT NOT NULL,updated_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS messages(id TEXT PRIMARY KEY,conversation_id TEXT NOT NULL,role TEXT NOT NULL,content TEXT NOT NULL,created_at TEXT NOT NULL,tool_name TEXT NULL,FOREIGN KEY(conversation_id) REFERENCES conversations(id) ON DELETE CASCADE);
            CREATE INDEX IF NOT EXISTS ix_messages_conversation ON messages(conversation_id,created_at);
            CREATE TABLE IF NOT EXISTS projects(id TEXT PRIMARY KEY,name TEXT NOT NULL,workspace_path TEXT NOT NULL,instructions TEXT NOT NULL,created_at TEXT NOT NULL,updated_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS automations(id TEXT PRIMARY KEY,name TEXT NOT NULL,prompt TEXT NOT NULL,interval_minutes INTEGER NOT NULL,enabled INTEGER NOT NULL,last_run_at TEXT NULL,next_run_at TEXT NOT NULL,last_status TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS schema_migrations(version INTEGER PRIMARY KEY,name TEXT NOT NULL,applied_at TEXT NOT NULL,checksum TEXT NOT NULL);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        var baseline = connection.CreateCommand();
        baseline.CommandText = "INSERT OR IGNORE INTO schema_migrations(version,name,applied_at,checksum) VALUES(12,'v12_baseline',$now,$checksum)";
        baseline.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        baseline.Parameters.AddWithValue("$checksum", Sha256("v12_baseline"));
        await baseline.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task VerifyBaselineAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        foreach (var table in BaselineTables)
        {
            var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name";
            command.Parameters.AddWithValue("$name", table);
            if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) != 1)
                throw new InvalidDataException($"V1.2 数据表 {table} 缺失，迁移已停止");
        }
        await VerifyIntegrityAsync(connection, cancellationToken);
    }

    private static async Task<bool> HasMigrationAsync(SqliteConnection connection, int version,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT checksum FROM schema_migrations WHERE version=$version";
        command.Parameters.AddWithValue("$version", version);
        var value = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (value is null) return false;
        if (value != Sha256(MigrationSql)) throw new InvalidDataException("迁移 013 校验和不一致，启动已停止");
        return true;
    }

    private string CreateBackup(SqliteConnection source)
    {
        var directory = Path.GetDirectoryName(database.DatabasePath)!;
        var path = Path.Combine(directory, $"workpilot.pre-v13.{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.db");
        using var backup = new SqliteConnection($"Data Source={path}");
        backup.Open();
        source.BackupDatabase(backup);
        foreach (var old in Directory.GetFiles(directory, "workpilot.pre-v13.*.db")
                     .OrderByDescending(File.GetLastWriteTimeUtc).Skip(3))
        {
            try { File.Delete(old); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { AppLogger.Error("Old migration backup cleanup failed", error); }
        }
        return path;
    }

    private void RestoreBackup(string backupPath)
    {
        var failedPath = database.DatabasePath + ".failed-v13." + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
        File.Move(database.DatabasePath, failedPath);
        using var source = new SqliteConnection($"Data Source={backupPath};Mode=ReadOnly");
        using var destination = new SqliteConnection($"Data Source={database.DatabasePath}");
        source.Open(); destination.Open(); source.BackupDatabase(destination);
        AppLogger.Error("Post-migration integrity check failed; pre-V1.3 backup restored");
    }

    private static async Task ApplyV13Async(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var defaultId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow.ToString("O");
        var foreignKeys = connection.CreateCommand(); foreignKeys.CommandText = "PRAGMA foreign_keys=OFF";
        await foreignKeys.ExecuteNonQueryAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var command = connection.CreateCommand(); command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = MigrationSql;
            command.Parameters.AddWithValue("$space", defaultId); command.Parameters.AddWithValue("$now", now);
            command.Parameters.AddWithValue("$checksum", Sha256(MigrationSql));
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch { await transaction.RollbackAsync(cancellationToken); throw; }
        finally
        {
            var enable = connection.CreateCommand(); enable.CommandText = "PRAGMA foreign_keys=ON";
            await enable.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task VerifyIntegrityAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        foreach (var pragma in new[] { "PRAGMA integrity_check", "PRAGMA foreign_key_check" })
        {
            var command = connection.CreateCommand(); command.CommandText = pragma;
            var value = await command.ExecuteScalarAsync(cancellationToken);
            if (pragma.Contains("integrity", StringComparison.Ordinal) && !string.Equals(value?.ToString(), "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("SQLite 完整性检查失败，数据库未继续打开");
            if (pragma.Contains("foreign", StringComparison.Ordinal) && value is not null)
                throw new InvalidDataException("SQLite 外键检查失败，数据库未继续打开");
        }
    }

    private static string Sha256(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private const string MigrationSql = """
        CREATE TABLE spaces(id TEXT PRIMARY KEY,name TEXT NOT NULL,description TEXT NOT NULL DEFAULT '',color_token TEXT NOT NULL,is_default INTEGER NOT NULL DEFAULT 0 CHECK(is_default IN(0,1)),is_archived INTEGER NOT NULL DEFAULT 0 CHECK(is_archived IN(0,1)),created_at TEXT NOT NULL,updated_at TEXT NOT NULL,row_version INTEGER NOT NULL DEFAULT 1);
        CREATE UNIQUE INDEX ux_spaces_one_default ON spaces(is_default) WHERE is_default=1;
        INSERT INTO spaces(id,name,description,color_token,is_default,is_archived,created_at,updated_at) VALUES($space,'我的空间','','green',1,0,$now,$now);
        CREATE TABLE projects_v13(id TEXT PRIMARY KEY,space_id TEXT NOT NULL REFERENCES spaces(id) ON DELETE RESTRICT,name TEXT NOT NULL,workspace_path TEXT NOT NULL,instructions TEXT NOT NULL,ignore_rules TEXT NOT NULL DEFAULT '',include_hidden INTEGER NOT NULL DEFAULT 0 CHECK(include_hidden IN(0,1)),created_at TEXT NOT NULL,updated_at TEXT NOT NULL,row_version INTEGER NOT NULL DEFAULT 1);
        INSERT INTO projects_v13(id,space_id,name,workspace_path,instructions,created_at,updated_at) SELECT id,$space,name,workspace_path,instructions,created_at,updated_at FROM projects;
        CREATE TABLE conversations_v13(id TEXT PRIMARY KEY,space_id TEXT NOT NULL REFERENCES spaces(id) ON DELETE RESTRICT,project_id TEXT NULL REFERENCES projects_v13(id) ON DELETE SET NULL,title TEXT NOT NULL,created_at TEXT NOT NULL,updated_at TEXT NOT NULL);
        INSERT INTO conversations_v13(id,space_id,project_id,title,created_at,updated_at) SELECT id,$space,NULL,title,created_at,updated_at FROM conversations;
        DROP TABLE projects; DROP TABLE conversations; ALTER TABLE projects_v13 RENAME TO projects; ALTER TABLE conversations_v13 RENAME TO conversations;
        CREATE INDEX ix_projects_space_updated ON projects(space_id,updated_at DESC); CREATE INDEX ix_conversations_space_updated ON conversations(space_id,updated_at DESC); CREATE INDEX ix_conversations_project ON conversations(project_id,updated_at DESC);
        CREATE TABLE tasks(id TEXT PRIMARY KEY,space_id TEXT NOT NULL REFERENCES spaces(id) ON DELETE RESTRICT,project_id TEXT NULL REFERENCES projects(id) ON DELETE SET NULL,main_conversation_id TEXT NULL REFERENCES conversations(id) ON DELETE SET NULL,title TEXT NOT NULL,description TEXT NOT NULL DEFAULT '',status TEXT NOT NULL CHECK(status IN('backlog','todo','in_progress','blocked','done','cancelled')),priority TEXT NOT NULL CHECK(priority IN('low','normal','high','urgent')),due_date TEXT NULL,sort_key INTEGER NOT NULL,completed_at TEXT NULL,created_at TEXT NOT NULL,updated_at TEXT NOT NULL,row_version INTEGER NOT NULL DEFAULT 1,CHECK((status='done' AND completed_at IS NOT NULL) OR(status<>'done' AND completed_at IS NULL)));
        CREATE UNIQUE INDEX ux_tasks_main_conversation ON tasks(main_conversation_id) WHERE main_conversation_id IS NOT NULL; CREATE INDEX ix_tasks_space_status_sort ON tasks(space_id,status,sort_key); CREATE INDEX ix_tasks_project_status ON tasks(project_id,status,updated_at DESC); CREATE INDEX ix_tasks_due ON tasks(space_id,due_date) WHERE due_date IS NOT NULL;
        CREATE TABLE assets(id INTEGER PRIMARY KEY AUTOINCREMENT,public_id TEXT NOT NULL UNIQUE,project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,normalized_path TEXT NOT NULL,path_key TEXT NOT NULL,display_path TEXT NOT NULL,file_name TEXT NOT NULL,extension TEXT NOT NULL,category TEXT NOT NULL CHECK(category IN('code','document','data','config','other')),size_bytes INTEGER NOT NULL CHECK(size_bytes>=0),modified_unix_ms INTEGER NOT NULL,quick_fingerprint TEXT NOT NULL,sha256 TEXT NULL,text_status TEXT NOT NULL CHECK(text_status IN('indexed','metadata_only_type','metadata_only_size_limit','unsupported_encoding','read_error','missing')),generation INTEGER NOT NULL,last_seen_at TEXT NOT NULL,created_at TEXT NOT NULL,updated_at TEXT NOT NULL,UNIQUE(project_id,path_key));
        CREATE INDEX ix_assets_project_generation ON assets(project_id,generation); CREATE INDEX ix_assets_project_modified ON assets(project_id,modified_unix_ms DESC); CREATE INDEX ix_assets_project_extension ON assets(project_id,extension); CREATE INDEX ix_assets_file_name ON assets(file_name COLLATE NOCASE);
        CREATE TABLE asset_chunks(id INTEGER PRIMARY KEY AUTOINCREMENT,asset_id INTEGER NOT NULL REFERENCES assets(id) ON DELETE CASCADE,ordinal INTEGER NOT NULL,start_offset INTEGER NOT NULL,end_offset INTEGER NOT NULL,token_estimate INTEGER NOT NULL,content TEXT NOT NULL,search_text TEXT NOT NULL,file_name_tokens TEXT NOT NULL,path_tokens TEXT NOT NULL,content_hash TEXT NOT NULL,UNIQUE(asset_id,ordinal));
        CREATE INDEX ix_asset_chunks_asset ON asset_chunks(asset_id,ordinal); CREATE VIRTUAL TABLE asset_chunks_fts USING fts5(search_text,file_name_tokens,path_tokens,content='asset_chunks',content_rowid='id',tokenize='unicode61 remove_diacritics 2');
        CREATE TRIGGER asset_chunks_ai AFTER INSERT ON asset_chunks BEGIN INSERT INTO asset_chunks_fts(rowid,search_text,file_name_tokens,path_tokens) VALUES(new.id,new.search_text,new.file_name_tokens,new.path_tokens); END;
        CREATE TRIGGER asset_chunks_ad AFTER DELETE ON asset_chunks BEGIN INSERT INTO asset_chunks_fts(asset_chunks_fts,rowid,search_text,file_name_tokens,path_tokens) VALUES('delete',old.id,old.search_text,old.file_name_tokens,old.path_tokens); END;
        CREATE TRIGGER asset_chunks_au AFTER UPDATE ON asset_chunks BEGIN INSERT INTO asset_chunks_fts(asset_chunks_fts,rowid,search_text,file_name_tokens,path_tokens) VALUES('delete',old.id,old.search_text,old.file_name_tokens,old.path_tokens); INSERT INTO asset_chunks_fts(rowid,search_text,file_name_tokens,path_tokens) VALUES(new.id,new.search_text,new.file_name_tokens,new.path_tokens); END;
        CREATE TABLE asset_index_state(project_id TEXT PRIMARY KEY REFERENCES projects(id) ON DELETE CASCADE,status TEXT NOT NULL CHECK(status IN('idle','discovering','scanning','ready','paused','limit_reached','error')),generation INTEGER NOT NULL DEFAULT 0,discovered_count INTEGER NOT NULL DEFAULT 0,processed_count INTEGER NOT NULL DEFAULT 0,indexed_text_count INTEGER NOT NULL DEFAULT 0,skipped_count INTEGER NOT NULL DEFAULT 0,error_count INTEGER NOT NULL DEFAULT 0,current_path TEXT NULL,last_full_scan_at TEXT NULL,last_event_at TEXT NULL,last_error_code TEXT NULL,last_error_message TEXT NULL,updated_at TEXT NOT NULL);
        INSERT INTO settings(key,value) VALUES('active_space_id',$space) ON CONFLICT(key) DO UPDATE SET value=$space;
        INSERT INTO schema_migrations(version,name,applied_at,checksum) VALUES(13,'013_spaces_tasks_assets',$now,$checksum);
        """;
}
