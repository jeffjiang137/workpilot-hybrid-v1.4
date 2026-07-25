using WorkPilot.Contracts.Primitives;
using Xunit;

namespace WorkPilot.Contracts.Tests;

public sealed class LimitsTests
{
    [Fact]
    public void Interval_bounds_are_well_ordered()
    {
        Assert.True(Limits.V1_5.MinIntervalSeconds > 0);
        Assert.True(Limits.V1_5.MinIntervalSeconds < Limits.V1_5.MaxIntervalSeconds);
    }

    [Fact]
    public void Workflow_and_payload_bounds_are_positive_and_sane()
    {
        Assert.InRange(Limits.V1_5.MaxWorkflowEdges, 1, 1000);
        Assert.InRange(Limits.V1_5.MaxWorkflowNodes, 1, 1000);
        Assert.True(Limits.V1_5.MaxPayloadBytes > 0);
        Assert.True(Limits.V1_5.MaxConcurrentRunsPerHost > 0);
        Assert.True(Limits.V1_5.MaxRetryAttempts > 0);
        Assert.True(Limits.V1_5.MaxEntityIdLength > 0);
    }

    [Fact]
    public void Schedule_bounds_match_spec()
    {
        // spec doc 03 §2.1: interval 60–2,592,000
        Assert.Equal(60, Limits.V1_5.MinIntervalSeconds);
        Assert.Equal(2_592_000, Limits.V1_5.MaxIntervalSeconds);
        // spec doc 04 §2.2: candidate dates up to 5 years
        Assert.Equal(5, Limits.V1_5.MaxCalendarHorizonYears);
        // schema: nodes 1–32, edges 0–64
        Assert.Equal(32, Limits.V1_5.MaxWorkflowNodes);
        Assert.Equal(64, Limits.V1_5.MaxWorkflowEdges);
        // RUN-010: catch_up caps at 5
        Assert.Equal(5, Limits.V1_5.MaxCatchUpRuns);
    }

    [Fact]
    public void Lease_bounds_enclose_default()
    {
        Assert.InRange(Limits.V1_5.DefaultLeaseSeconds, 1, Limits.V1_5.MaxLeaseSeconds);
    }

    [Fact]
    public void ContractVersions_are_published_and_non_empty()
    {
        Assert.Equal("1.5.0", ContractVersions.ContractsVersion);
        Assert.True(ContractVersions.AutomationDefinitionSchema >= 1);
        Assert.True(ContractVersions.PolicySchema >= 1);
        Assert.True(ContractVersions.RunEventSchema >= 1);
        Assert.False(string.IsNullOrEmpty(ContractVersions.SchedulerAlgorithm));
        Assert.False(string.IsNullOrEmpty(ContractVersions.PermissionAlgorithm));
        Assert.False(string.IsNullOrEmpty(ContractVersions.RedactionAlgorithm));
        Assert.False(string.IsNullOrEmpty(ContractVersions.AuditIntegrityAlgorithm));
    }
}
