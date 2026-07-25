using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using WorkPilot.Application.Security.Governance;
using WorkPilot.Contracts.Primitives;

namespace WorkPilot.Infrastructure.Security;

/// <summary>
/// SQLite implementation of <see cref="ISecurityStateStore"/> (doc 06 §6.4). A display-name-free
/// key/value store for governance flags (currently <c>emergency_stop</c>). Holds only governance
/// counters — never secrets. Backed by Migration 021 <c>security_state</c>.
/// </summary>
public sealed class SecurityStateSqliteStore : ISecurityStateStore
{
    private readonly SqliteConnection _connection;

    public SecurityStateSqliteStore(SqliteConnection connection)
    {
        _connection = connection ?? throw new System.ArgumentNullException(nameof(connection));
    }

    public async Task<Result> SetAsync(string key, string value, CancellationToken ct = default)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "INSERT INTO security_state(key,value) VALUES($k,$v) " +
                          "ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        await cmd.ExecuteNonQueryAsync(ct);
        return Result.Success();
    }

    public async Task<Result<string?>> GetAsync(string key, CancellationToken ct = default)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM security_state WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        var v = await cmd.ExecuteScalarAsync(ct);
        return Result<string?>.Ok(v is null ? null : (string)v);
    }
}
