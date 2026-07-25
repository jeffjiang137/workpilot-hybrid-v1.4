using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace WorkPilot.Infrastructure.Data;

/// <summary>
/// SQLite implementation of <see cref="ISchemaVersionProbe"/>. Tolerates a missing
/// <c>schema_migrations</c> table (returns 0) so a fresh or foreign database is classified as
/// <see cref="SchemaCompatibilityKind.Empty"/> rather than erroring (T23, MIG-A06/A07).
/// </summary>
public sealed class SqliteSchemaVersionProbe : ISchemaVersionProbe
{
    public async Task<int> GetCurrentVersionAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        await using (var exists = connection.CreateCommand())
        {
            exists.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='schema_migrations'";
            if (await exists.ExecuteScalarAsync(cancellationToken) is null)
                return 0;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(version),0) FROM schema_migrations";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? 0 : Convert.ToInt32(value);
    }
}
