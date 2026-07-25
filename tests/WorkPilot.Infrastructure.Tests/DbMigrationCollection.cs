using Xunit;

/// <summary>
/// Serializes tests that exercise the full <see cref="V15DatabaseMigrator.InitializeAsync"/> path
/// (which calls <c>BackupDatabase</c> on a shared temp directory). Running these in parallel causes
/// SQLite "database is locked" races on the generated <c>workpilot.pre-v17.*.db</c> backup files.
/// </summary>
[CollectionDefinition("DbMigration")]
public sealed class DbMigrationCollectionDefinition
{
}
