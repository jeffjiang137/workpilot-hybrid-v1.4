namespace WorkPilot.Domain.Schema;

/// <summary>
/// Coarse maintenance posture surfaced to the UI/Host after a schema-version handshake (T23,
/// PKG-A06). <see cref="None"/> means the database is ready; any other value means the app should
/// show a maintenance banner and must not enable or run automations.
/// </summary>
public enum MaintenanceState
{
    /// <summary>Schema is compatible; normal operation.</summary>
    None,
    /// <summary>Database is older than this binary; the App will forward-migrate (transient) or must be launched.</summary>
    UpgradeRequired,
    /// <summary>Database was produced by a newer binary; the user must upgrade this app.</summary>
    DatabaseTooNew,
    /// <summary>Host encountered an un-migrated/older/newer schema and must not proceed (MIG-A07).</summary>
    HostSchemaMismatch,
    /// <summary>Checksum/integrity verification failed (tampered or corrupt DDL); refuse and restore backup.</summary>
    ChecksumMismatch,
    /// <summary>An upgrade is in progress; enabling/running automations is suspended (PKG-A06).</summary>
    MaintenanceInProgress
}

/// <summary>Pure mapping from a handshake verdict to a maintenance posture (T23).</summary>
public static class MaintenanceStateMapper
{
    public static MaintenanceState FromCompatibility(SchemaCompatibilityKind kind, bool isHost)
    {
        return kind switch
        {
            SchemaCompatibilityKind.Compatible => MaintenanceState.None,
            SchemaCompatibilityKind.Empty or SchemaCompatibilityKind.NeedsMigration
                => isHost ? MaintenanceState.HostSchemaMismatch : MaintenanceState.UpgradeRequired,
            SchemaCompatibilityKind.IncompatibleNewer => MaintenanceState.DatabaseTooNew,
            SchemaCompatibilityKind.HostUnsupported => MaintenanceState.HostSchemaMismatch,
            SchemaCompatibilityKind.MigrationFailed => MaintenanceState.ChecksumMismatch,
            _ => MaintenanceState.None
        };
    }
}
