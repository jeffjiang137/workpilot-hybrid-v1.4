using System.IO.Compression;
using System.Text;
using Microsoft.Data.Sqlite;
using WorkPilot.Services;

static void CreateZip(string path, IReadOnlyDictionary<string, string> files)
{
    using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
    foreach (var pair in files)
    {
        var entry = archive.CreateEntry(pair.Key, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(pair.Value);
    }
}

var root = Path.Combine(Path.GetTempPath(), "workpilot-v13-migration-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root); var path = Path.Combine(root, "fixture.db");
try
{
    await using (var connection = new SqliteConnection($"Data Source={path}"))
    {
        await connection.OpenAsync(); var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys=ON;
            CREATE TABLE settings(key TEXT PRIMARY KEY,value TEXT NOT NULL);
            CREATE TABLE conversations(id TEXT PRIMARY KEY,title TEXT NOT NULL,created_at TEXT NOT NULL,updated_at TEXT NOT NULL);
            CREATE TABLE messages(id TEXT PRIMARY KEY,conversation_id TEXT NOT NULL,role TEXT NOT NULL,content TEXT NOT NULL,created_at TEXT NOT NULL,tool_name TEXT NULL,FOREIGN KEY(conversation_id) REFERENCES conversations(id) ON DELETE CASCADE);
            CREATE TABLE projects(id TEXT PRIMARY KEY,name TEXT NOT NULL,workspace_path TEXT NOT NULL,instructions TEXT NOT NULL,created_at TEXT NOT NULL,updated_at TEXT NOT NULL);
            CREATE TABLE automations(id TEXT PRIMARY KEY,name TEXT NOT NULL,prompt TEXT NOT NULL,interval_minutes INTEGER NOT NULL,enabled INTEGER NOT NULL,last_run_at TEXT NULL,next_run_at TEXT NOT NULL,last_status TEXT NOT NULL);
            INSERT INTO projects VALUES('p1','旧项目','C:\fixture','说明','2026-01-01T00:00:00+00:00','2026-01-01T00:00:00+00:00');
            INSERT INTO conversations VALUES('c1','旧会话','2026-01-01T00:00:00+00:00','2026-01-01T00:00:00+00:00');
            INSERT INTO messages VALUES('m1','c1','user','fixture','2026-01-01T00:00:00+00:00',NULL);
            INSERT INTO settings VALUES('model','fixture-model');
            """;
        await command.ExecuteNonQueryAsync();
    }
    var database = new DatabaseService(path); await database.InitializeAsync(); await database.InitializeAsync();
    await database.EnsureSafeIndexRuntimeAsync();
    await using var verify = await database.OpenConnectionAsync();
    foreach (var table in new[] { "spaces", "tasks", "assets", "asset_chunks", "asset_chunks_fts", "asset_index_state",
        "experts", "expert_revisions", "skills", "connector_accounts", "mcp_servers", "mcp_capabilities", "capability_audit" })
    {
        var command = verify.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name=$name";
        command.Parameters.AddWithValue("$name", table);
        if (Convert.ToInt32(await command.ExecuteScalarAsync()) != 1) throw new InvalidOperationException($"Missing {table}");
    }
    var count = verify.CreateCommand(); count.CommandText = "SELECT COUNT(*) FROM projects p JOIN spaces s ON s.id=p.space_id WHERE p.id='p1' AND s.is_default=1";
    if (Convert.ToInt32(await count.ExecuteScalarAsync()) != 1) throw new InvalidOperationException("V1.2 project was not migrated");
    var message = verify.CreateCommand(); message.CommandText = "SELECT COUNT(*) FROM messages WHERE id='m1' AND conversation_id='c1'";
    if (Convert.ToInt32(await message.ExecuteScalarAsync()) != 1) throw new InvalidOperationException("V1.2 message was not preserved");
    var versions = verify.CreateCommand(); versions.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version BETWEEN 13 AND 16";
    if (Convert.ToInt32(await versions.ExecuteScalarAsync()) != 4) throw new InvalidOperationException("V1.4 migrations are incomplete");
    var expert = verify.CreateCommand(); expert.CommandText = "SELECT COUNT(*) FROM experts WHERE is_builtin=1 AND current_revision_id IS NOT NULL";
    if (Convert.ToInt32(await expert.ExecuteScalarAsync()) != 1) throw new InvalidOperationException("Default expert missing");
    await verify.CloseAsync();

    var validZip = Path.Combine(root, "valid-skill.zip");
    CreateZip(validZip, new Dictionary<string, string>
    {
        ["workpilot-skill.json"] = """{"schemaVersion":1,"id":"acme.prd","name":"PRD 助手","publisher":"Acme","version":"1.0.0","description":"整理 PRD","entrypoint":"SKILL.md","minWorkPilotVersion":"1.4.0","activation":{"aliases":["写PRD"],"tags":["prd"]},"requiredCapabilities":[]}""",
        ["SKILL.md"] = "# PRD\n输出可验收需求。"
    });
    var skills = new SkillService(database, Path.Combine(root, "skill-data"));
    var inspection = await skills.InspectAsync(validZip);
    var installed = await skills.InstallAsync(inspection.Token);
    if (installed.Id != "acme.prd" || (await skills.ListAsync()).Count != 1)
        throw new InvalidOperationException("Valid skill package was not installed");

    var maliciousZip = Path.Combine(root, "malicious-skill.zip");
    CreateZip(maliciousZip, new Dictionary<string, string>
    {
        ["../../outside.exe"] = "unsafe", ["workpilot-skill.json"] = "{}", ["SKILL.md"] = "unsafe"
    });
    var rejected = false;
    try { await skills.InspectAsync(maliciousZip); }
    catch (InvalidDataException) { rejected = true; }
    if (!rejected || File.Exists(Path.Combine(root, "outside.exe")))
        throw new InvalidOperationException("Malicious skill path was not safely rejected");
    Console.WriteLine("WorkPilot.Integration.Tests passed");
}
finally
{
    try { Directory.Delete(root, true); }
    catch (IOException error) { Console.Error.WriteLine($"Fixture cleanup warning: {error.Message}"); }
    catch (UnauthorizedAccessException error) { Console.Error.WriteLine($"Fixture cleanup warning: {error.Message}"); }
}
