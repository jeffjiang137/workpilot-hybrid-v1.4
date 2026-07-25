using System.Collections.Generic;
using WorkPilot.Domain.PermissionGovernance;
using WorkPilot.Domain.PermissionGovernance.Evaluation;
using Xunit;

namespace WorkPilot.Domain.Tests.PermissionGovernance;

public class ScopeIntersectorTests
{
    [Fact]
    public void Null_operand_passes_through_as_unbounded()
    {
        var s = new LocalProjectScope("p", new[] { "/src" }, new[] { "read" });
        var r = ScopeIntersector.Intersect(null, s);
        Assert.Equal(ScopeIntersector.Kind.Bounded, r.Outcome);
        Assert.Same(s, r.Scope);
    }

    [Fact]
    public void LocalProject_same_project_overlapping_roots_intersect()
    {
        var a = new LocalProjectScope("p", new[] { "/src", "/docs" }, new[] { "read" });
        var b = new LocalProjectScope("p", new[] { "/src" }, new[] { "read", "write" });
        var r = ScopeIntersector.Intersect(a, b);
        Assert.Equal(ScopeIntersector.Kind.Bounded, r.Outcome);
        var lp = Assert.IsType<LocalProjectScope>(r.Scope);
        Assert.Equal("p", lp.ProjectId);
        Assert.Equal(new[] { "/src" }, lp.RelativeRoots);
        Assert.Equal(new[] { "read" }, lp.Operations);
    }

    [Fact]
    public void LocalProject_disjoint_roots_are_disjoint()
    {
        var a = new LocalProjectScope("p", new[] { "/src" }, new[] { "read" });
        var b = new LocalProjectScope("p", new[] { "/other" }, new[] { "read" });
        var r = ScopeIntersector.Intersect(a, b);
        Assert.Equal(ScopeIntersector.Kind.Disjoint, r.Outcome);
    }

    [Fact]
    public void LocalProject_different_project_is_disjoint()
    {
        var a = new LocalProjectScope("p1", new[] { "/src" }, new[] { "read" });
        var b = new LocalProjectScope("p2", new[] { "/src" }, new[] { "read" });
        Assert.Equal(ScopeIntersector.Kind.Disjoint, ScopeIntersector.Intersect(a, b).Outcome);
    }

    [Fact]
    public void Empty_roots_mean_unrestricted_and_intersect_to_other()
    {
        var a = new LocalProjectScope("p", System.Array.Empty<string>(), new[] { "read" });
        var b = new LocalProjectScope("p", new[] { "/src" }, new[] { "read", "write" });
        var r = ScopeIntersector.Intersect(a, b);
        Assert.Equal(ScopeIntersector.Kind.Bounded, r.Outcome);
        var lp = Assert.IsType<LocalProjectScope>(r.Scope);
        Assert.Equal(new[] { "/src" }, lp.RelativeRoots); // governed by b
    }

    [Fact]
    public void Different_scope_kinds_are_disjoint()
    {
        var a = new LocalProjectScope("p", new[] { "/src" }, new[] { "read" });
        var b = new GitHubScope("acc", new[] { "owner/repo" }, new[] { "read" });
        Assert.Equal(ScopeIntersector.Kind.Disjoint, ScopeIntersector.Intersect(a, b).Outcome);
    }

    [Fact]
    public void GitHub_matching_account_intersects_repositories()
    {
        var a = new GitHubScope("acc", new[] { "o/r1", "o/r2" }, new[] { "read" });
        var b = new GitHubScope("acc", new[] { "o/r2", "o/r3" }, new[] { "read", "write" });
        var r = ScopeIntersector.Intersect(a, b);
        Assert.Equal(ScopeIntersector.Kind.Bounded, r.Outcome);
        var g = Assert.IsType<GitHubScope>(r.Scope);
        Assert.Equal(new[] { "o/r2" }, g.Repositories);
    }

    [Fact]
    public void Mcp_mismatched_schema_is_disjoint()
    {
        var a = new McpScope("srv", "cap", "sha1", "{}");
        var b = new McpScope("srv", "cap", "sha2", "{}");
        Assert.Equal(ScopeIntersector.Kind.Disjoint, ScopeIntersector.Intersect(a, b).Outcome);
    }

    [Fact]
    public void Builtin_matching_capability_intersects_entities()
    {
        var a = new BuiltinScope("cap", new[] { "e1", "e2" });
        var b = new BuiltinScope("cap", new[] { "e2", "e3" });
        var r = ScopeIntersector.Intersect(a, b);
        Assert.Equal(ScopeIntersector.Kind.Bounded, r.Outcome);
        var bs = Assert.IsType<BuiltinScope>(r.Scope);
        Assert.Equal(new[] { "e2" }, bs.EntityIds);
    }
}
