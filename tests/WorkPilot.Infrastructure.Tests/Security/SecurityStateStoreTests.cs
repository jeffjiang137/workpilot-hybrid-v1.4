using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Infrastructure.Data;
using WorkPilot.Infrastructure.Security;
using Xunit;

namespace WorkPilot.Infrastructure.Tests.Security;

/// <summary>Round-trip tests for <see cref="SecurityStateSqliteStore"/> and <see cref="SqliteRevocationEpoch"/>
/// against the Migration 021 tables (doc 06 §6.4 / doc 07 §11).</summary>
public sealed class SecurityStateStoreTests
{
    private static async Task<SqliteConnection> OpenConnectionAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var migrator = new V15DatabaseMigrator(null);
        await migrator.CreateSecurityStateTablesAsync(connection, CancellationToken.None);
        return connection;
    }

    [Fact]
    public async Task Security_state_set_get_round_trips_and_upserts()
    {
        await using var connection = await OpenConnectionAsync();
        var store = new SecurityStateSqliteStore(connection);

        Assert.True((await store.SetAsync("emergency_stop", "true", CancellationToken.None)).IsSuccess);
        var got = await store.GetAsync("emergency_stop", CancellationToken.None);
        Assert.True(got.IsSuccess);
        Assert.Equal("true", got.Value);

        // Upsert: updating the same key replaces the value (no duplicate row).
        Assert.True((await store.SetAsync("emergency_stop", "false", CancellationToken.None)).IsSuccess);
        var updated = await store.GetAsync("emergency_stop", CancellationToken.None);
        Assert.Equal("false", updated.Value);
    }

    [Fact]
    public async Task Security_state_missing_key_returns_null()
    {
        await using var connection = await OpenConnectionAsync();
        var store = new SecurityStateSqliteStore(connection);

        var missing = await store.GetAsync("does_not_exist", CancellationToken.None);
        Assert.True(missing.IsSuccess);
        Assert.Null(missing.Value);
    }

    [Fact]
    public async Task Revocation_epoch_starts_at_zero_and_bump_persists_across_instances()
    {
        await using var connection = await OpenConnectionAsync();

        var first = new SqliteRevocationEpoch(connection);
        Assert.Equal(0, first.Current);

        first.Bump();
        first.Bump();
        Assert.Equal(2, first.Current);

        // A fresh instance reading the same connection must observe the persisted epoch.
        var second = new SqliteRevocationEpoch(connection);
        Assert.Equal(2, second.Current);

        second.Bump();
        Assert.Equal(3, second.Current);
    }
}
