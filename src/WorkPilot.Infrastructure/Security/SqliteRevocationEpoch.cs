using System;
using Microsoft.Data.Sqlite;
using WorkPilot.Application.Permission.Policy;

namespace WorkPilot.Infrastructure.Security;

/// <summary>
/// SQLite-backed process-wide revocation epoch (doc 07 §11/§15/§17). A single-row counter in the
/// Migration 021 <c>revocation_epoch</c> table. <see cref="Current"/> is cached in memory; <see cref="Bump"/>
/// increments the cache and persists atomically so every previously-issued permit / consent receipt /
/// automation grant fails its Current-State Check (doc 07 §11). Shares the application's single
/// SQLite connection; governance commands run sequentially on the UI thread so sync persistence is safe.
/// </summary>
public sealed class SqliteRevocationEpoch : IRevocationEpoch
{
    private readonly SqliteConnection _connection;
    private long _epoch;
    private readonly object _lock = new();

    public SqliteRevocationEpoch(SqliteConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT epoch FROM revocation_epoch LIMIT 1";
        var v = cmd.ExecuteScalar();
        _epoch = v is null ? 0L : Convert.ToInt64(v);
    }

    public long Current { get { lock (_lock) return _epoch; } }

    public void Bump()
    {
        lock (_lock)
        {
            _epoch++;
            using var tx = _connection.BeginTransaction();
            try
            {
                var cmd = _connection.CreateCommand();
                cmd.Transaction = (SqliteTransaction)tx;
                cmd.CommandText = "UPDATE revocation_epoch SET epoch=$e";
                cmd.Parameters.AddWithValue("$e", _epoch);
                var rows = cmd.ExecuteNonQuery();
                if (rows == 0) // table not yet seeded (e.g. created via the test-only schema helper)
                {
                    var ins = _connection.CreateCommand();
                    ins.Transaction = (SqliteTransaction)tx;
                    ins.CommandText = "INSERT INTO revocation_epoch(epoch) VALUES($e)";
                    ins.Parameters.AddWithValue("$e", _epoch);
                    ins.ExecuteNonQuery();
                }
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
    }
}
