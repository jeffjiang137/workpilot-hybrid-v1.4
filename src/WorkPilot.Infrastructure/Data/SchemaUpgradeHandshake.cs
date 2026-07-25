using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Domain.Schema;
using WorkPilot.Infrastructure.Clock;

namespace WorkPilot.Infrastructure.Data;

/// <summary>
/// Startup handshake that compares a database's applied schema version against this binary's
/// expectations and either approves opening, forward-migrates (App only), or refuses (T23,
/// MIG-A06/A07). The Host never migrates and only accepts an exact match, so an un-migrated or
/// newer database makes the Host exit cleanly instead of corrupting or mis-reading the schema.
/// </summary>
public sealed class SchemaUpgradeHandshake
{
    private readonly int _expectedVersion;
    private readonly int _hostMinimumVersion;
    private readonly ISchemaVersionProbe _probe;
    private readonly V15DatabaseMigrator _migrator;
    private readonly IClock _clock;

    public SchemaUpgradeHandshake(
        int expectedVersion,
        int hostMinimumVersion,
        ISchemaVersionProbe probe,
        V15DatabaseMigrator migrator,
        IClock? clock = null)
    {
        _expectedVersion = expectedVersion;
        _hostMinimumVersion = hostMinimumVersion;
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _migrator = migrator ?? throw new ArgumentNullException(nameof(migrator));
        _clock = clock ?? new SystemClock();
    }

    public async Task<HandshakeResult> PerformAsync(SqliteConnection connection, bool isHost, CancellationToken cancellationToken = default)
    {
        var databaseVersion = await _probe.GetCurrentVersionAsync(connection, cancellationToken);
        var initial = SchemaCompatibilityClassifier.Classify(databaseVersion, _expectedVersion, _hostMinimumVersion, isHost);

        if (isHost)
        {
            // The Host never migrates and only operates on an exactly-matching schema. A fresh/older
            // DB means the App has not migrated yet; a newer DB means this Host binary is too old.
            // Either way the Host exits cleanly (MIG-A07) without touching the schema.
            return initial.Kind == SchemaCompatibilityKind.Compatible
                ? HandshakeResult.Ready(initial)
                : HandshakeResult.Refused(initial);
        }

        // App path: always run the migrator. It is idempotent and re-verifies every applied migration's
        // checksum on each startup (MIG-A06), and forward-migrates an older database when a path exists.
        try
        {
            await _migrator.InitializeAsync(connection, cancellationToken);
        }
        catch (InvalidDataException ex)
        {
            // Checksum mismatch / orphan revision / corrupt DDL. Refuse startup.
            return HandshakeResult.Refused(new SchemaCompatibility(
                SchemaCompatibilityKind.MigrationFailed,
                databaseVersion, _expectedVersion, _hostMinimumVersion,
                SchemaCompatibilityCodes.ChecksumMismatch,
                new System.Collections.Generic.Dictionary<string, string> { ["detail"] = ex.Message }));
        }

        var after = await _probe.GetCurrentVersionAsync(connection, cancellationToken);
        var verified = SchemaCompatibilityClassifier.Classify(after, _expectedVersion, _hostMinimumVersion, isHost);
        return verified.Kind == SchemaCompatibilityKind.Compatible
            ? HandshakeResult.Ready(verified)
            : HandshakeResult.Refused(verified);
    }
}

/// <summary>Result of a <see cref="SchemaUpgradeHandshake"/>: approved-to-open or refused.</summary>
public sealed record HandshakeResult(bool Success, SchemaCompatibility Compatibility, string? ErrorCode = null)
{
    public static HandshakeResult Ready(SchemaCompatibility compatibility) => new(true, compatibility);
    public static HandshakeResult Refused(SchemaCompatibility compatibility) => new(false, compatibility, compatibility.MessageKey);
}
