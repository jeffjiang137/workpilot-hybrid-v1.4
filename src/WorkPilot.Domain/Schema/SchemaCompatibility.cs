using System.Collections.Generic;
using System.Globalization;

namespace WorkPilot.Domain.Schema;

/// <summary>Outcome of comparing a database's applied schema version against this binary's expectations.</summary>
public enum SchemaCompatibilityKind
{
    /// <summary>No migrations applied yet (fresh or pre-V1.5 database).</summary>
    Empty,
    /// <summary>Database schema matches the expected version; safe to open.</summary>
    Compatible,
    /// <summary>Database is older than expected but a forward-migration path exists (App only).</summary>
    NeedsMigration,
    /// <summary>Database was produced by a newer binary; this binary must not open it.</summary>
    IncompatibleNewer,
    /// <summary>Host context encountered a schema it does not manage (never migrates, requires exact match).</summary>
    HostUnsupported,
    /// <summary>Migration or checksum verification failed (tampered/corrupt DDL). Startup must refuse.</summary>
    MigrationFailed
}

/// <summary>Immutable description of a database schema vs. this binary's expectations (T23 handshake).</summary>
public sealed record SchemaCompatibility(
    SchemaCompatibilityKind Kind,
    int DatabaseVersion,
    int ExpectedVersion,
    int HostMinimumVersion,
    string MessageKey,
    IReadOnlyDictionary<string, string>? SafeDetails = null)
{
    /// <summary>True when the calling process may proceed to open the database.</summary>
    public bool MayProceed => Kind is SchemaCompatibilityKind.Compatible
        or SchemaCompatibilityKind.NeedsMigration
        or SchemaCompatibilityKind.Empty;
}

/// <summary>
/// Pure classifier that maps a database's applied version + caller role to a compatibility verdict.
/// No I/O, no clock, no external state — fully deterministic (T23, MIG-A06/A07).
/// </summary>
public static class SchemaCompatibilityClassifier
{
    public const int EmptyVersion = 0;

    public static SchemaCompatibility Classify(int databaseVersion, int expectedVersion, int hostMinimumVersion, bool isHost)
    {
        var db = databaseVersion < 0 ? EmptyVersion : databaseVersion;
        var hostMin = hostMinimumVersion.ToString(CultureInfo.InvariantCulture);

        if (isHost)
        {
            // The Host never migrates and only operates on an exactly-expected schema. A fresh (0) or
            // older DB means the App has not migrated yet -> Host exits. A newer DB means this Host
            // binary is too old -> Host exits. Either way the Host must not touch the schema (MIG-A07).
            if (db == expectedVersion)
                return Compatible(db, expectedVersion, hostMinimumVersion);
            if (db == EmptyVersion)
                return Make(SchemaCompatibilityKind.HostUnsupported, db, expectedVersion, hostMinimumVersion,
                    SchemaCompatibilityCodes.HostDatabaseNotInitialized, ("host_minimum_version", hostMin));
            if (db < expectedVersion)
                return Make(SchemaCompatibilityKind.HostUnsupported, db, expectedVersion, hostMinimumVersion,
                    SchemaCompatibilityCodes.HostSchemaTooOld, ("host_minimum_version", hostMin));
            return Make(SchemaCompatibilityKind.HostUnsupported, db, expectedVersion, hostMinimumVersion,
                SchemaCompatibilityCodes.HostSchemaNewerThanBinary, ("host_minimum_version", hostMin));
        }

        if (db == expectedVersion)
            return Compatible(db, expectedVersion, hostMinimumVersion);
        if (db == EmptyVersion)
            return Make(SchemaCompatibilityKind.Empty, db, expectedVersion, hostMinimumVersion,
                SchemaCompatibilityCodes.DatabaseEmptyNeedsMigration);
        if (db < expectedVersion)
            return Make(SchemaCompatibilityKind.NeedsMigration, db, expectedVersion, hostMinimumVersion,
                SchemaCompatibilityCodes.DatabaseNeedsMigration,
                ("database_version", db.ToString(CultureInfo.InvariantCulture)));
        return Make(SchemaCompatibilityKind.IncompatibleNewer, db, expectedVersion, hostMinimumVersion,
            SchemaCompatibilityCodes.DatabaseNewerThanApp,
            ("database_version", db.ToString(CultureInfo.InvariantCulture)));
    }

    private static SchemaCompatibility Compatible(int db, int expected, int hostMin) =>
        Make(SchemaCompatibilityKind.Compatible, db, expected, hostMin, SchemaCompatibilityCodes.DatabaseCompatible);

    private static SchemaCompatibility Make(
        SchemaCompatibilityKind kind, int db, int expected, int hostMin, string code,
        params (string Key, string Value)[] details) =>
        new(kind, db, expected, hostMin, code,
            details.Length == 0 ? null : details.ToDictionary(d => d.Key, d => d.Value));
}

/// <summary>Stable message keys for schema-compatibility verdicts (localized by the UI layer).</summary>
public static class SchemaCompatibilityCodes
{
    public const string DatabaseCompatible = "SCHEMA_DATABASE_COMPATIBLE";
    public const string DatabaseEmptyNeedsMigration = "SCHEMA_DATABASE_EMPTY_NEEDS_MIGRATION";
    public const string DatabaseNeedsMigration = "SCHEMA_DATABASE_NEEDS_MIGRATION";
    public const string DatabaseNewerThanApp = "SCHEMA_DATABASE_NEWER_THAN_APP";
    public const string HostDatabaseNotInitialized = "SCHEMA_HOST_DATABASE_NOT_INITIALIZED";
    public const string HostSchemaTooOld = "SCHEMA_HOST_SCHEMA_TOO_OLD";
    public const string HostSchemaNewerThanBinary = "SCHEMA_HOST_SCHEMA_NEWER_THAN_BINARY";
    public const string ChecksumMismatch = "SCHEMA_CHECKSUM_MISMATCH";
}
