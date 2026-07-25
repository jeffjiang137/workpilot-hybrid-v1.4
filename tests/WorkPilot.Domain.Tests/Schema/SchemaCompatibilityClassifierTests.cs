using WorkPilot.Domain.Schema;
using Xunit;

namespace WorkPilot.Domain.Tests.Schema;

public class SchemaCompatibilityClassifierTests
{
    private const int Expected = 22;
    private const int HostMin = 22;

    [Fact]
    public void Fresh_database_is_Empty_for_app()
    {
        var result = SchemaCompatibilityClassifier.Classify(0, Expected, HostMin, isHost: false);
        Assert.Equal(SchemaCompatibilityKind.Empty, result.Kind);
        Assert.True(result.MayProceed);
        Assert.Equal(SchemaCompatibilityCodes.DatabaseEmptyNeedsMigration, result.MessageKey);
    }

    [Fact]
    public void Older_database_is_NeedsMigration_for_app()
    {
        var result = SchemaCompatibilityClassifier.Classify(16, Expected, HostMin, isHost: false);
        Assert.Equal(SchemaCompatibilityKind.NeedsMigration, result.Kind);
        Assert.True(result.MayProceed);
        Assert.Equal("16", result.SafeDetails!["database_version"]);
    }

    [Fact]
    public void Matching_database_is_Compatible_for_app()
    {
        var result = SchemaCompatibilityClassifier.Classify(22, Expected, HostMin, isHost: false);
        Assert.Equal(SchemaCompatibilityKind.Compatible, result.Kind);
        Assert.True(result.MayProceed);
    }

    [Fact]
    public void Newer_database_is_IncompatibleNewer_for_app()
    {
        var result = SchemaCompatibilityClassifier.Classify(23, Expected, HostMin, isHost: false);
        Assert.Equal(SchemaCompatibilityKind.IncompatibleNewer, result.Kind);
        Assert.False(result.MayProceed);
        Assert.Equal("23", result.SafeDetails!["database_version"]);
    }

    [Fact]
    public void Fresh_database_is_HostUnsupported_for_host()
    {
        var result = SchemaCompatibilityClassifier.Classify(0, Expected, HostMin, isHost: true);
        Assert.Equal(SchemaCompatibilityKind.HostUnsupported, result.Kind);
        Assert.False(result.MayProceed);
        Assert.Equal(SchemaCompatibilityCodes.HostDatabaseNotInitialized, result.MessageKey);
    }

    [Fact]
    public void Older_database_is_HostUnsupported_for_host()
    {
        var result = SchemaCompatibilityClassifier.Classify(16, Expected, HostMin, isHost: true);
        Assert.Equal(SchemaCompatibilityKind.HostUnsupported, result.Kind);
        Assert.False(result.MayProceed);
        Assert.Equal(SchemaCompatibilityCodes.HostSchemaTooOld, result.MessageKey);
    }

    [Fact]
    public void Matching_database_is_Compatible_for_host()
    {
        var result = SchemaCompatibilityClassifier.Classify(22, Expected, HostMin, isHost: true);
        Assert.Equal(SchemaCompatibilityKind.Compatible, result.Kind);
        Assert.True(result.MayProceed);
    }

    [Fact]
    public void Newer_database_is_HostUnsupported_for_host()
    {
        var result = SchemaCompatibilityClassifier.Classify(23, Expected, HostMin, isHost: true);
        Assert.Equal(SchemaCompatibilityKind.HostUnsupported, result.Kind);
        Assert.False(result.MayProceed);
        Assert.Equal(SchemaCompatibilityCodes.HostSchemaNewerThanBinary, result.MessageKey);
    }

    [Fact]
    public void Negative_database_version_is_treated_as_empty()
    {
        var result = SchemaCompatibilityClassifier.Classify(-5, Expected, HostMin, isHost: false);
        Assert.Equal(SchemaCompatibilityKind.Empty, result.Kind);
        Assert.Equal(0, result.DatabaseVersion);
    }
}
