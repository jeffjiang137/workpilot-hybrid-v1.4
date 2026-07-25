using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace WorkPilot.Infrastructure.Data;

/// <summary>Reads the highest applied schema migration version from a database (T23 handshake).</summary>
public interface ISchemaVersionProbe
{
    /// <summary>
    /// Returns <c>MAX(version)</c> from <c>schema_migrations</c>, or 0 when the table/row is absent
    /// (fresh or foreign database). Never throws for a missing table.
    /// </summary>
    Task<int> GetCurrentVersionAsync(SqliteConnection connection, CancellationToken cancellationToken = default);
}
