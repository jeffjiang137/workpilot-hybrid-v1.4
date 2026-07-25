using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Infrastructure.Data;
using Xunit;

namespace WorkPilot.Infrastructure.Tests;

internal sealed class FakeClock(DateTimeOffset fixedTime) : IClock
{
    public DateTimeOffset UtcNow => fixedTime;
    public DateTimeOffset Now => fixedTime;
}

[Collection("DbMigration")]
public sealed class Migration017IntegrationTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static async Task<(string Path, SqliteConnection Connection)> BuildMigratedAsync(Action<SqliteConnection> seed)
    {
        var path = Path.Combine(Path.GetTempPath(), $"wp_mig_{Guid.NewGuid():N}.db");
        var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using (var setup = connection.CreateCommand())
        {
            setup.CommandText = V14Schema;
            await setup.ExecuteNonQueryAsync(TestContext.Current);
        }

        await using (var tx = await connection.BeginTransactionAsync(TestContext.Current))
        {
            const string space = "space-default";
            var insertSpace = connection.CreateCommand();
            insertSpace.CommandText = "INSERT INTO spaces(id,name,description,color_token,is_default,created_at,updated_at) VALUES($id,'默认空间','','green',1,$now,$now)";
            insertSpace.Parameters.AddWithValue("$id", space);
            insertSpace.Parameters.AddWithValue("$now", FixedNow.ToString("O"));
            insertSpace.Transaction = (SqliteTransaction)tx;
            await insertSpace.ExecuteNonQueryAsync(TestContext.Current);

            var insertSetting = connection.CreateCommand();
            insertSetting.CommandText = "INSERT INTO settings(key,value) VALUES('active_space_id',$id)";
            insertSetting.Parameters.AddWithValue("$id", space);
            insertSetting.Transaction = (SqliteTransaction)tx;
            await insertSetting.ExecuteNonQueryAsync(TestContext.Current);

            await tx.CommitAsync(TestContext.Current);
        }

        seed(connection);

        var migrator = new V15DatabaseMigrator(new FakeClock(FixedNow));
        await migrator.InitializeAsync(connection, TestContext.Current);
        return (path, connection);
    }

    private static void SeedAutomation(SqliteConnection connection, string id, string name, string prompt,
        int intervalMinutes, bool enabled, string? nextRunAt, string lastStatus = "never_run")
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO automations(id,name,prompt,interval_minutes,enabled,last_run_at,next_run_at,last_status)
            VALUES($id,$name,$prompt,$interval,$enabled,$lastRun,$nextRun,$status)
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$prompt", prompt);
        command.Parameters.AddWithValue("$interval", intervalMinutes);
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("$lastRun", DBNull.Value);
        command.Parameters.AddWithValue("$nextRun", (object?)nextRunAt ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", lastStatus);
        command.ExecuteNonQuery();
    }

    private static async Task<int> CountAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    [Fact]
    public async Task Legacy_rows_converted_to_definitions_without_data_loss()
    {
        var (path, connection) = await BuildMigratedAsync(c =>
        {
            SeedAutomation(c, "a1", "日报", "生成今日日报", 30, true, "2026-06-01T00:00:00Z");
            SeedAutomation(c, "a2", "周报", "生成本周周报", 10080, false, "2026-06-08T00:00:00Z");
        });

        try
        {
            Assert.Equal(2, await CountAsync(connection, "SELECT COUNT(*) FROM automation_definitions", TestContext.Current));
            Assert.Equal(2, await CountAsync(connection, "SELECT COUNT(*) FROM automation_revisions", TestContext.Current));
            Assert.Equal(2, await CountAsync(connection, "SELECT COUNT(*) FROM automation_schedules", TestContext.Current));

            var command = connection.CreateCommand();
            command.CommandText = "SELECT id,name,current_revision_id FROM automation_definitions ORDER BY id";
            await using var reader = await command.ExecuteReaderAsync(TestContext.Current);
            var definitions = new List<(string Id, string Name, string? Revision)>();
            while (await reader.ReadAsync(TestContext.Current))
                definitions.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));

            Assert.Contains(definitions, d => d.Id == "a1" && d.Name == "日报" && d.Revision is not null);
            Assert.Contains(definitions, d => d.Id == "a2" && d.Name == "周报" && d.Revision is not null);
        }
        finally
        {
            connection.Dispose();
            Cleanup(path);
        }
    }

    [Fact]
    public async Task Workflow_preserves_legacy_prompt_as_agent_prompt_instruction()
    {
        var (path, connection) = await BuildMigratedAsync(c =>
            SeedAutomation(c, "a1", "日报", "生成今日日报内容", 30, true, "2026-06-01T00:00:00Z"));

        try
        {
            var command = connection.CreateCommand();
            command.CommandText = "SELECT workflow_json FROM automation_revisions WHERE automation_id='a1'";
            var workflow = JsonNode.Parse((string)(await command.ExecuteScalarAsync(TestContext.Current))!)!;
            Assert.Equal("agent_prompt_1", workflow["entry_node_id"]!.GetValue<string>());
            var node = workflow["nodes"]![0]!;
            Assert.Equal("agent_prompt", node["kind"]!.GetValue<string>());
            Assert.Equal("生成今日日报内容", node["instruction_template"]!.GetValue<string>());
            Assert.Equal("result", node["output_key"]!.GetValue<string>());
        }
        finally
        {
            connection.Dispose();
            Cleanup(path);
        }
    }

    [Fact]
    public async Task Legacy_table_preserved_and_untouched()
    {
        var (path, connection) = await BuildMigratedAsync(c =>
            SeedAutomation(c, "a1", "日报", "p", 30, true, "2026-06-01T00:00:00Z"));

        try
        {
            Assert.Equal(1, await CountAsync(connection, "SELECT COUNT(*) FROM automations_v12_legacy", TestContext.Current));
            var command = connection.CreateCommand();
            command.CommandText = "SELECT name,prompt,interval_minutes,enabled FROM automations_v12_legacy WHERE id='a1'";
            await using var reader = await command.ExecuteReaderAsync(TestContext.Current);
            Assert.True(await reader.ReadAsync(TestContext.Current));
            Assert.Equal("日报", reader.GetString(0));
            Assert.Equal("p", reader.GetString(1));
            Assert.Equal(30, reader.GetInt32(2));
            Assert.Equal(1, reader.GetInt32(3));
        }
        finally
        {
            connection.Dispose();
            Cleanup(path);
        }
    }

    [Fact]
    public async Task No_orphan_current_revision_after_conversion()
    {
        var (path, connection) = await BuildMigratedAsync(c =>
        {
            SeedAutomation(c, "a1", "日报", "p", 30, true, "2026-06-01T00:00:00Z");
            SeedAutomation(c, "a2", "周报", "p", 60, false, "2026-06-08T00:00:00Z");
        });

        try
        {
            Assert.Equal(0, await CountAsync(connection,
                "SELECT COUNT(*) FROM automation_definitions WHERE lifecycle<>'draft' AND current_revision_id IS NULL",
                TestContext.Current));
            Assert.Equal(0, await CountAsync(connection,
                "SELECT COUNT(*) FROM automation_definitions ad LEFT JOIN automation_revisions ar ON ad.current_revision_id=ar.id WHERE ad.current_revision_id IS NOT NULL AND ar.id IS NULL",
                TestContext.Current));
            Assert.Equal(2, await CountAsync(connection,
                "SELECT COUNT(*) FROM automation_definitions d JOIN automation_revisions r ON d.current_revision_id=r.id WHERE r.automation_id=d.id",
                TestContext.Current));
        }
        finally
        {
            connection.Dispose();
            Cleanup(path);
        }
    }

    [Fact]
    public async Task Canonical_sha256_is_64_hex_characters()
    {
        var (path, connection) = await BuildMigratedAsync(c =>
            SeedAutomation(c, "a1", "日报", "p", 30, true, "2026-06-01T00:00:00Z"));

        try
        {
            var command = connection.CreateCommand();
            command.CommandText = "SELECT canonical_sha256 FROM automation_revisions";
            await using var reader = await command.ExecuteReaderAsync(TestContext.Current);
            while (await reader.ReadAsync(TestContext.Current))
            {
                var hash = reader.GetString(0);
                Assert.Equal(64, hash.Length);
                Assert.True(IsHex(hash));
            }
        }
        finally
        {
            connection.Dispose();
            Cleanup(path);
        }
    }

    [Fact]
    public async Task Legacy_automation_without_binding_becomes_paused_needs_review()
    {
        var (path, connection) = await BuildMigratedAsync(c =>
        {
            SeedAutomation(c, "a1", "启用", "p", 30, true, "2026-06-01T00:00:00Z");
            SeedAutomation(c, "a2", "禁用", "p", 60, false, "2026-06-08T00:00:00Z");
        });

        try
        {
            var command = connection.CreateCommand();
            command.CommandText = "SELECT id,lifecycle FROM automation_definitions ORDER BY id";
            await using var reader = await command.ExecuteReaderAsync(TestContext.Current);
            var lifecycles = new Dictionary<string, string>();
            while (await reader.ReadAsync(TestContext.Current))
                lifecycles[reader.GetString(0)] = reader.GetString(1);

            Assert.Equal("paused_needs_review", lifecycles["a1"]);
            Assert.Equal("paused_needs_review", lifecycles["a2"]);
        }
        finally
        {
            connection.Dispose();
            Cleanup(path);
        }
    }

    [Fact]
    public async Task Anchor_parsed_from_next_run_at_when_valid()
    {
        var (path, connection) = await BuildMigratedAsync(c =>
            SeedAutomation(c, "a1", "日报", "p", 30, true, "2026-06-01T00:00:00Z"));

        try
        {
            var command = connection.CreateCommand();
            command.CommandText = "SELECT trigger_json FROM automation_revisions WHERE automation_id='a1'";
            var trigger = JsonNode.Parse((string)(await command.ExecuteScalarAsync(TestContext.Current))!)!;
            Assert.Equal(1800, trigger["interval_seconds"]!.GetValue<int>());
            var anchor = DateTimeOffset.Parse(trigger["anchor_at_utc"]!.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
            Assert.Equal(new DateTimeOffset(2026, 5, 31, 23, 30, 0, TimeSpan.Zero), anchor);
        }
        finally
        {
            connection.Dispose();
            Cleanup(path);
        }
    }

    [Fact]
    public async Task Anchor_falls_back_to_now_when_next_run_at_unparseable()
    {
        var (path, connection) = await BuildMigratedAsync(c =>
            SeedAutomation(c, "a1", "无锚点", "p", 30, true, "invalid-date-format"));

        try
        {
            var command = connection.CreateCommand();
            command.CommandText = "SELECT trigger_json,lifecycle FROM automation_revisions r JOIN automation_definitions d ON r.automation_id=d.id WHERE r.automation_id='a1'";
            await using var reader = await command.ExecuteReaderAsync(TestContext.Current);
            Assert.True(await reader.ReadAsync(TestContext.Current));
            var trigger = JsonNode.Parse(reader.GetString(0))!;
            var anchor = DateTimeOffset.Parse(trigger["anchor_at_utc"]!.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
            Assert.Equal(FixedNow, anchor.ToUniversalTime());
            Assert.Equal("paused_needs_review", reader.GetString(1));
        }
        finally
        {
            connection.Dispose();
            Cleanup(path);
        }
    }

    [Fact]
    public async Task Interval_clamped_to_bounds()
    {
        var (path, connection) = await BuildMigratedAsync(c =>
        {
            SeedAutomation(c, "low", "下限", "p", 0, true, "2026-06-01T00:00:00Z");
            SeedAutomation(c, "high", "上限", "p", 20000, true, "2026-06-01T00:00:00Z");
        });

        try
        {
            var command = connection.CreateCommand();
            command.CommandText = "SELECT automation_id,trigger_json FROM automation_revisions ORDER BY automation_id";
            await using var reader = await command.ExecuteReaderAsync(TestContext.Current);
            var intervals = new Dictionary<string, int>();
            while (await reader.ReadAsync(TestContext.Current))
            {
                var trigger = JsonNode.Parse(reader.GetString(1))!;
                intervals[reader.GetString(0)] = trigger["interval_seconds"]!.GetValue<int>();
            }

            Assert.Equal(60, intervals["low"]);
            Assert.Equal(10080 * 60, intervals["high"]);
        }
        finally
        {
            connection.Dispose();
            Cleanup(path);
        }
    }

    [Fact]
    public async Task Rerun_is_idempotent_and_does_not_duplicate()
    {
        var (path, connection) = await BuildMigratedAsync(c =>
            SeedAutomation(c, "a1", "日报", "p", 30, true, "2026-06-01T00:00:00Z"));

        try
        {
            await new V15DatabaseMigrator(new FakeClock(FixedNow)).InitializeAsync(connection, TestContext.Current);
            Assert.Equal(1, await CountAsync(connection, "SELECT COUNT(*) FROM automation_definitions", TestContext.Current));
            Assert.Equal(1, await CountAsync(connection, "SELECT COUNT(*) FROM automation_revisions", TestContext.Current));
            Assert.Equal(1, await CountAsync(connection, "SELECT COUNT(*) FROM migration_legacy_automation_state", TestContext.Current));
        }
        finally
        {
            connection.Dispose();
            Cleanup(path);
        }
    }

    [Fact]
    public async Task Migration_records_checksum_in_schema_migrations()
    {
        var (path, connection) = await BuildMigratedAsync(c =>
            SeedAutomation(c, "a1", "日报", "p", 30, true, "2026-06-01T00:00:00Z"));

        try
        {
            var command = connection.CreateCommand();
            command.CommandText = "SELECT version,checksum FROM schema_migrations WHERE version=17";
            await using var reader = await command.ExecuteReaderAsync(TestContext.Current);
            Assert.True(await reader.ReadAsync(TestContext.Current));
            Assert.Equal(17, reader.GetInt32(0));
            var checksum = reader.GetString(1);
            Assert.Equal(64, checksum.Length);
            Assert.True(IsHex(checksum));
        }
        finally
        {
            connection.Dispose();
            Cleanup(path);
        }
    }

    [Fact]
    public async Task Backup_created_during_migration()
    {
        var (path, connection) = await BuildMigratedAsync(c =>
            SeedAutomation(c, "a1", "日报", "p", 30, true, "2026-06-01T00:00:00Z"));

        try
        {
            var directory = Path.GetDirectoryName(path)!;
            Assert.Contains(Directory.GetFiles(directory, "workpilot.pre-v17.*.db"), f => f != path);
        }
        finally
        {
            connection.Dispose();
            Cleanup(path);
        }
    }

    private static bool IsHex(string value)
    {
        foreach (var character in value)
            if (!Uri.IsHexDigit(character)) return false;
        return true;
    }

    private static void Cleanup(string path)
    {
        try { File.Delete(path); } catch { }
        var directory = Path.GetDirectoryName(path)!;
        foreach (var backup in Directory.GetFiles(directory, "workpilot.pre-v17.*.db"))
            try { File.Delete(backup); } catch { }
        foreach (var failed in Directory.GetFiles(directory, "*.failed-v17.*"))
            try { File.Delete(failed); } catch { }
    }

    private const string V14Schema = """
        CREATE TABLE spaces(id TEXT PRIMARY KEY,name TEXT NOT NULL,description TEXT NOT NULL DEFAULT '',color_token TEXT NOT NULL,is_default INTEGER NOT NULL DEFAULT 0 CHECK(is_default IN(0,1)),is_archived INTEGER NOT NULL DEFAULT 0 CHECK(is_archived IN(0,1)),created_at TEXT NOT NULL,updated_at TEXT NOT NULL,row_version INTEGER NOT NULL DEFAULT 1);
        CREATE TABLE settings(key TEXT PRIMARY KEY,value TEXT NOT NULL);
        CREATE TABLE automations(id TEXT PRIMARY KEY,name TEXT NOT NULL,prompt TEXT NOT NULL,interval_minutes INTEGER NOT NULL,enabled INTEGER NOT NULL,last_run_at TEXT NULL,next_run_at TEXT NOT NULL,last_status TEXT NOT NULL);
        CREATE TABLE schema_migrations(version INTEGER PRIMARY KEY,name TEXT NOT NULL,applied_at TEXT NOT NULL,checksum TEXT NOT NULL);
        PRAGMA foreign_keys=ON;
        """;
}

internal static class TestContext
{
    public static CancellationToken Current => CancellationToken.None;
}
