using WorkPilot.Domain.Schema;
using Xunit;

namespace WorkPilot.Domain.Tests.Schema;

public class MaintenanceStateMapperTests
{
    [Fact]
    public void Compatible_maps_to_None()
        => Assert.Equal(MaintenanceState.None, MaintenanceStateMapper.FromCompatibility(SchemaCompatibilityKind.Compatible, isHost: false));

    [Fact]
    public void Empty_database_maps_to_UpgradeRequired_for_app()
        => Assert.Equal(MaintenanceState.UpgradeRequired, MaintenanceStateMapper.FromCompatibility(SchemaCompatibilityKind.Empty, isHost: false));

    [Fact]
    public void NeedsMigration_maps_to_UpgradeRequired_for_app()
        => Assert.Equal(MaintenanceState.UpgradeRequired, MaintenanceStateMapper.FromCompatibility(SchemaCompatibilityKind.NeedsMigration, isHost: false));

    [Fact]
    public void Older_database_maps_to_HostSchemaMismatch_for_host()
        => Assert.Equal(MaintenanceState.HostSchemaMismatch, MaintenanceStateMapper.FromCompatibility(SchemaCompatibilityKind.NeedsMigration, isHost: true));

    [Fact]
    public void Fresh_database_maps_to_HostSchemaMismatch_for_host()
        => Assert.Equal(MaintenanceState.HostSchemaMismatch, MaintenanceStateMapper.FromCompatibility(SchemaCompatibilityKind.Empty, isHost: true));

    [Fact]
    public void IncompatibleNewer_maps_to_DatabaseTooNew()
        => Assert.Equal(MaintenanceState.DatabaseTooNew, MaintenanceStateMapper.FromCompatibility(SchemaCompatibilityKind.IncompatibleNewer, isHost: false));

    [Fact]
    public void HostUnsupported_maps_to_HostSchemaMismatch()
        => Assert.Equal(MaintenanceState.HostSchemaMismatch, MaintenanceStateMapper.FromCompatibility(SchemaCompatibilityKind.HostUnsupported, isHost: true));

    [Fact]
    public void MigrationFailed_maps_to_ChecksumMismatch()
        => Assert.Equal(MaintenanceState.ChecksumMismatch, MaintenanceStateMapper.FromCompatibility(SchemaCompatibilityKind.MigrationFailed, isHost: false));
}
