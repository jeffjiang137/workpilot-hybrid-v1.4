using System;
using System.Threading;
using Microsoft.Data.Sqlite;
using WorkPilot.Infrastructure.Data;
using Xunit;

namespace WorkPilot.Infrastructure.Tests.Permission.Policy;

/// <summary>T16: Migration 019 policy-governance tables are created idempotently with checksum verification.</summary>
public sealed class Migration019Tests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static async Task<SqliteConnection> BuildMigratedAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var setup = connection.CreateCommand())
        {
            // Minimal V1.4 schema sufficient for the 017→018→019 chain (PRAGMA FK is deferred at DDL time).
            setup.CommandText = """
                CREATE TABLE spaces(id TEXT PRIMARY KEY,name TEXT NOT NULL,description TEXT NOT NULL DEFAULT '',color_token TEXT NOT NULL,is_default INTEGER NOT NULL DEFAULT 0 CHECK(is_default IN(0,1)),is_archived INTEGER NOT NULL DEFAULT 0 CHECK(is_archived IN(0,1)),created_at TEXT NOT NULL,updated_at TEXT NOT NULL,row_version INTEGER NOT NULL DEFAULT 1);
                CREATE TABLE settings(key TEXT PRIMARY KEY,value TEXT NOT NULL);
                CREATE TABLE automations(id TEXT PRIMARY KEY,name TEXT NOT NULL,prompt TEXT NOT NULL,interval_minutes INTEGER NOT NULL,enabled INTEGER NOT NULL,last_run_at TEXT NULL,next_run_at TEXT NOT NULL,last_status TEXT NOT NULL);
                CREATE TABLE schema_migrations(version INTEGER PRIMARY KEY,name TEXT NOT NULL,applied_at TEXT NOT NULL,checksum TEXT NOT NULL);
                """;
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

        var migrator = new V15DatabaseMigrator(new FakeClock(FixedNow));
        await migrator.InitializeAsync(connection, TestContext.Current);
        return connection;
    }

    [Fact]
    public async Task Migration019_creates_policy_tables()
    {
        var connection = await BuildMigratedAsync();
        try
        {
            foreach (var table in new[] { "policy_documents", "policy_versions", "policy_statements", "policy_grants", "consent_receipts", "policy_audit" })
            {
                var cmd = connection.CreateCommand();
                cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
                var count = Convert.ToInt32(await cmd.ExecuteScalarAsync(TestContext.Current));
                Assert.Equal(0, count); // tables exist, empty
            }
        }
        finally
        {
            connection.Close();
        }
    }

    [Fact]
    public async Task Migration019_is_idempotent()
    {
        var connection = await BuildMigratedAsync();
        try
        {
            // Second apply must not insert a duplicate migration row.
            var migrator = new V15DatabaseMigrator(new FakeClock(FixedNow));
            await migrator.InitializeAsync(connection, TestContext.Current);

            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version=19";
            Assert.Equal(1, Convert.ToInt32(await cmd.ExecuteScalarAsync(TestContext.Current)));
        }
        finally
        {
            connection.Close();
        }
    }

    [Fact]
    public async Task Migration019_checksum_mismatch_throws()
    {
        var connection = await BuildMigratedAsync();
        try
        {
            await using (var tx = await connection.BeginTransactionAsync(TestContext.Current))
            {
                var corrupt = connection.CreateCommand();
                corrupt.CommandText = "UPDATE schema_migrations SET checksum='corrupted-sha' WHERE version=19";
                corrupt.Transaction = (SqliteTransaction)tx;
                await corrupt.ExecuteNonQueryAsync(TestContext.Current);
                await tx.CommitAsync(TestContext.Current);
            }

            var migrator = new V15DatabaseMigrator(new FakeClock(FixedNow));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                migrator.InitializeAsync(connection, TestContext.Current));
        }
        finally
        {
            connection.Close();
        }
    }
}
