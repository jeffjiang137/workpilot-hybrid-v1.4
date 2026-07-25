using System.Globalization;
using System.IO;
using System.Threading;
using Microsoft.Data.Sqlite;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Domain.Schema;
using WorkPilot.Infrastructure.Data;
using Xunit;

namespace WorkPilot.Infrastructure.Tests.Data;

[Collection("DbMigration")]
public sealed class SchemaUpgradeHandshakeTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const int Expected = V15DatabaseMigrator.LatestVersion; // 22

    private static async Task<(string Path, SqliteConnection Connection)> BuildBaselineAsync(Action<SqliteConnection>? seed = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"wp_handshake_{Guid.NewGuid():N}.db");
        var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = V14Schema;
            await setup.ExecuteNonQueryAsync(TestContext.Current);
        }

        await using (var tx = await connection.BeginTransactionAsync(TestContext.Current))
        {
            var insertSpace = connection.CreateCommand();
            insertSpace.CommandText = "INSERT INTO spaces(id,name,description,color_token,is_default,created_at,updated_at) VALUES($id,'默认空间','','green',1,$now,$now)";
            insertSpace.Parameters.AddWithValue("$id", "space-default");
            insertSpace.Parameters.AddWithValue("$now", FixedNow.ToString("O"));
            insertSpace.Transaction = (SqliteTransaction)tx;
            await insertSpace.ExecuteNonQueryAsync(TestContext.Current);

            var insertSetting = connection.CreateCommand();
            insertSetting.CommandText = "INSERT INTO settings(key,value) VALUES('active_space_id',$id)";
            insertSetting.Parameters.AddWithValue("$id", "space-default");
            insertSetting.Transaction = (SqliteTransaction)tx;
            await insertSetting.ExecuteNonQueryAsync(TestContext.Current);
            await tx.CommitAsync(TestContext.Current);
        }

        seed?.Invoke(connection);
        return (path, connection);
    }

    private static async Task MigrateToLatestAsync(SqliteConnection connection)
    {
        var migrator = new V15DatabaseMigrator(new FakeClock(FixedNow));
        await migrator.InitializeAsync(connection, TestContext.Current);
    }

    private static async Task<int> GetVersionAsync(SqliteConnection connection)
        => await new SqliteSchemaVersionProbe().GetCurrentVersionAsync(connection, TestContext.Current);

    private static void SeedAutomation(SqliteConnection connection, string id, string name, string prompt,
        int intervalMinutes, bool enabled, string? nextRunAt)
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
        command.Parameters.AddWithValue("$status", "never_run");
        command.ExecuteNonQuery();
    }

    private static async Task SeedLegacyVersionsAsync(SqliteConnection connection, int from, int to)
    {
        for (var v = from; v <= to; v++)
        {
            var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO schema_migrations(version,name,applied_at,checksum) VALUES($v,$n,$now,$c)";
            command.Parameters.AddWithValue("$v", v);
            command.Parameters.AddWithValue("$n", $"legacy_{v}");
            command.Parameters.AddWithValue("$now", FixedNow.ToString("O"));
            command.Parameters.AddWithValue("$c", $"checksum-{v}");
            await command.ExecuteNonQueryAsync(TestContext.Current);
        }
    }

    // ---- ISchemaVersionProbe ----

    [Fact]
    public async Task Probe_returns_zero_when_table_absent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wp_probe_{Guid.NewGuid():N}.db");
        await using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        Assert.Equal(0, await GetVersionAsync(connection));
        Cleanup(path);
    }

    [Fact]
    public async Task Probe_returns_max_version()
    {
        var (path, connection) = await BuildBaselineAsync(c => SeedLegacyVersionsAsync(c, 12, 22).GetAwaiter().GetResult());
        try { Assert.Equal(22, await GetVersionAsync(connection)); }
        finally { connection.Dispose(); Cleanup(path); }
    }

    // ---- Handshake: App path ----

    [Fact]
    public async Task Handshake_fresh_legacy_db_migrates_to_expected_and_is_ready()
    {
        var (path, connection) = await BuildBaselineAsync(c => SeedAutomation(c, "a1", "日报", "p", 30, true, "2026-06-01T00:00:00Z"));
        try
        {
            var handshake = new SchemaUpgradeHandshake(Expected, Expected, new SqliteSchemaVersionProbe(),
                new V15DatabaseMigrator(new FakeClock(FixedNow)));
            var result = await handshake.PerformAsync(connection, isHost: false, TestContext.Current);

            Assert.True(result.Success);
            Assert.Equal(SchemaCompatibilityKind.Compatible, result.Compatibility.Kind);
            Assert.Equal(22, await GetVersionAsync(connection));
        }
        finally { connection.Dispose(); Cleanup(path); }
    }

    [Fact]
    public async Task Handshake_db_at_expected_is_ready_without_migrating()
    {
        var (path, connection) = await BuildBaselineAsync(c => SeedAutomation(c, "a1", "日报", "p", 30, true, "2026-06-01T00:00:00Z"));
        try
        {
            await MigrateToLatestAsync(connection);
            var before = await GetVersionAsync(connection);
            var handshake = new SchemaUpgradeHandshake(Expected, Expected, new SqliteSchemaVersionProbe(),
                new V15DatabaseMigrator(new FakeClock(FixedNow)));
            var result = await handshake.PerformAsync(connection, isHost: false, TestContext.Current);

            Assert.True(result.Success);
            Assert.Equal(SchemaCompatibilityKind.Compatible, result.Compatibility.Kind);
            Assert.Equal(before, await GetVersionAsync(connection)); // no-op, idempotent
        }
        finally { connection.Dispose(); Cleanup(path); }
    }

    [Fact]
    public async Task Handshake_db_older_than_app_forward_migrates()
    {
        var (path, connection) = await BuildBaselineAsync(c =>
        {
            SeedAutomation(c, "a1", "日报", "p", 30, true, "2026-06-01T00:00:00Z");
            SeedLegacyVersionsAsync(c, 12, 16).GetAwaiter().GetResult();
        });
        try
        {
            Assert.Equal(16, await GetVersionAsync(connection)); // pre-condition
            var handshake = new SchemaUpgradeHandshake(Expected, Expected, new SqliteSchemaVersionProbe(),
                new V15DatabaseMigrator(new FakeClock(FixedNow)));
            var result = await handshake.PerformAsync(connection, isHost: false, TestContext.Current);

            Assert.True(result.Success);
            Assert.Equal(22, await GetVersionAsync(connection));
        }
        finally { connection.Dispose(); Cleanup(path); }
    }

    [Fact]
    public async Task Handshake_db_newer_than_app_is_refused_and_untouched()
    {
        var (path, connection) = await BuildBaselineAsync(c => SeedAutomation(c, "a1", "日报", "p", 30, true, "2026-06-01T00:00:00Z"));
        try
        {
            await MigrateToLatestAsync(connection);
            await SeedLegacyVersionsAsync(connection, 23, 23); // simulate a newer binary that applied v23
            Assert.Equal(23, await GetVersionAsync(connection));

            var handshake = new SchemaUpgradeHandshake(Expected, Expected, new SqliteSchemaVersionProbe(),
                new V15DatabaseMigrator(new FakeClock(FixedNow)));
            var result = await handshake.PerformAsync(connection, isHost: false, TestContext.Current);

            Assert.False(result.Success);
            Assert.Equal(SchemaCompatibilityKind.IncompatibleNewer, result.Compatibility.Kind);
            Assert.Equal(23, await GetVersionAsync(connection)); // not modified / not downgraded
        }
        finally { connection.Dispose(); Cleanup(path); }
    }

    [Fact]
    public async Task Handshake_checksum_tamper_is_refused_as_migration_failed()
    {
        var (path, connection) = await BuildBaselineAsync(c => SeedAutomation(c, "a1", "日报", "p", 30, true, "2026-06-01T00:00:00Z"));
        try
        {
            await MigrateToLatestAsync(connection);
            // Tamper with a recorded checksum (MIG-A06).
            await using var corrupt = connection.CreateCommand();
            corrupt.CommandText = "UPDATE schema_migrations SET checksum='tampered' WHERE version=22";
            await corrupt.ExecuteNonQueryAsync(TestContext.Current);

            var handshake = new SchemaUpgradeHandshake(Expected, Expected, new SqliteSchemaVersionProbe(),
                new V15DatabaseMigrator(new FakeClock(FixedNow)));
            var result = await handshake.PerformAsync(connection, isHost: false, TestContext.Current);

            Assert.False(result.Success);
            Assert.Equal(SchemaCompatibilityKind.MigrationFailed, result.Compatibility.Kind);
            Assert.Equal(SchemaCompatibilityCodes.ChecksumMismatch, result.Compatibility.MessageKey);
            Assert.Equal(SchemaCompatibilityCodes.ChecksumMismatch, result.ErrorCode);
        }
        finally { connection.Dispose(); Cleanup(path); }
    }

    // ---- Handshake: Host path (MIG-A07) ----

    [Fact]
    public async Task Handshake_host_at_matching_schema_is_ready()
    {
        var (path, connection) = await BuildBaselineAsync(c => SeedAutomation(c, "a1", "日报", "p", 30, true, "2026-06-01T00:00:00Z"));
        try
        {
            await MigrateToLatestAsync(connection);
            var handshake = new SchemaUpgradeHandshake(Expected, Expected, new SqliteSchemaVersionProbe(),
                new V15DatabaseMigrator(new FakeClock(FixedNow)));
            var result = await handshake.PerformAsync(connection, isHost: true, TestContext.Current);

            Assert.True(result.Success);
            Assert.Equal(SchemaCompatibilityKind.Compatible, result.Compatibility.Kind);
        }
        finally { connection.Dispose(); Cleanup(path); }
    }

    [Fact]
    public async Task Handshake_host_with_older_unmigrated_db_is_refused_and_does_not_migrate()
    {
        var (path, connection) = await BuildBaselineAsync(c =>
        {
            SeedAutomation(c, "a1", "日报", "p", 30, true, "2026-06-01T00:00:00Z");
            SeedLegacyVersionsAsync(c, 12, 16).GetAwaiter().GetResult();
        });
        try
        {
            var handshake = new SchemaUpgradeHandshake(Expected, Expected, new SqliteSchemaVersionProbe(),
                new V15DatabaseMigrator(new FakeClock(FixedNow)));
            var result = await handshake.PerformAsync(connection, isHost: true, TestContext.Current);

            Assert.False(result.Success);
            Assert.Equal(SchemaCompatibilityKind.HostUnsupported, result.Compatibility.Kind);
            Assert.Equal(SchemaCompatibilityCodes.HostSchemaTooOld, result.Compatibility.MessageKey);
            Assert.Equal(16, await GetVersionAsync(connection)); // Host must NOT migrate
        }
        finally { connection.Dispose(); Cleanup(path); }
    }

    [Fact]
    public async Task Handshake_host_with_fresh_db_is_refused()
    {
        var (path, connection) = await BuildBaselineAsync(c => SeedAutomation(c, "a1", "日报", "p", 30, true, "2026-06-01T00:00:00Z"));
        try
        {
            var handshake = new SchemaUpgradeHandshake(Expected, Expected, new SqliteSchemaVersionProbe(),
                new V15DatabaseMigrator(new FakeClock(FixedNow)));
            var result = await handshake.PerformAsync(connection, isHost: true, TestContext.Current);

            Assert.False(result.Success);
            Assert.Equal(SchemaCompatibilityKind.HostUnsupported, result.Compatibility.Kind);
            Assert.Equal(SchemaCompatibilityCodes.HostDatabaseNotInitialized, result.Compatibility.MessageKey);
            Assert.Equal(0, await GetVersionAsync(connection)); // Host did not initialize the schema
        }
        finally { connection.Dispose(); Cleanup(path); }
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
