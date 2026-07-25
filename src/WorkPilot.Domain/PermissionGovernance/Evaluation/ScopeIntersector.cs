using System.Collections.Generic;
using System.Linq;

namespace WorkPilot.Domain.PermissionGovernance.Evaluation;

/// <summary>
/// Per-same-type intersection of <see cref="ResourceScope"/> (doc 07 §4). Different scope types
/// intersect to <b>disjoint</b> (which the evaluator turns into <c>Deny(ResourceOutOfScope)</c>).
/// An <see langword="null"/> operand means "unbounded / no constraint" and passes through. An empty
/// <c>RelativeRoots</c> / <c>Operations</c> / equivalent list means "unrestricted within the
/// project/account" (also passes through), so only a genuine disjoint overlap yields <see cref="Result.Disjoint"/>.
/// Path normalization to project root is performed by the C++ Core before compare (doc 07 §4); the
/// managed intersector compares normalized string lists exactly.
/// </summary>
public static class ScopeIntersector
{
    public enum Kind { Unbounded, Bounded, Disjoint }

    /// <summary>Intersects two scopes. <paramref name="scope"/> is non-null only when <see cref="Kind.Bounded"/>.</summary>
    public sealed record Result(Kind Outcome, ResourceScope? Scope)
    {
        public static readonly Result UnboundedResult = new(Kind.Unbounded, null);
        public static readonly Result DisjointResult = new(Kind.Disjoint, null);
        public static Result Bounded(ResourceScope s) => new(Kind.Bounded, s);
    }

    public static Result Intersect(ResourceScope? a, ResourceScope? b)
    {
        if (a is null && b is null)
            return Result.UnboundedResult;  // both unbounded → no constraint
        if (a is null)
            return Result.Bounded(b!);       // one side unbounded → governed by the other
        if (b is null)
            return Result.Bounded(a!);
        if (!string.Equals(a.Kind, b.Kind, System.StringComparison.Ordinal))
            return Result.DisjointResult;   // different types never overlap

        return (a, b) switch
        {
            (LocalProjectScope x, LocalProjectScope y) => IntersectLocal(x, y),
            (GitHubScope x, GitHubScope y) => IntersectGitHub(x, y),
            (NotionScope x, NotionScope y) => IntersectNotion(x, y),
            (McpScope x, McpScope y) => IntersectMcp(x, y),
            (BuiltinScope x, BuiltinScope y) => IntersectBuiltin(x, y),
            _ => Result.DisjointResult
        };
    }

    private static Result IntersectLocal(LocalProjectScope x, LocalProjectScope y)
    {
        if (!string.Equals(x.ProjectId, y.ProjectId, System.StringComparison.Ordinal))
            return Result.DisjointResult;
        var (rd, roots) = IntersectLists(x.RelativeRoots, y.RelativeRoots);
        if (rd) return Result.DisjointResult;
        var (od, ops) = IntersectLists(x.Operations, y.Operations);
        if (od) return Result.DisjointResult;
        return Result.Bounded(new LocalProjectScope(x.ProjectId, roots, ops));
    }

    private static Result IntersectGitHub(GitHubScope x, GitHubScope y)
    {
        if (!string.Equals(x.AccountId, y.AccountId, System.StringComparison.Ordinal))
            return Result.DisjointResult;
        var (rd, repos) = IntersectLists(x.Repositories, y.Repositories);
        if (rd) return Result.DisjointResult;
        var (od, ops) = IntersectLists(x.Operations, y.Operations);
        if (od) return Result.DisjointResult;
        return Result.Bounded(new GitHubScope(x.AccountId, repos, ops));
    }

    private static Result IntersectNotion(NotionScope x, NotionScope y)
    {
        if (!string.Equals(x.AccountId, y.AccountId, System.StringComparison.Ordinal))
            return Result.DisjointResult;
        var (rd, pages) = IntersectLists(x.PagesOrDatabases, y.PagesOrDatabases);
        if (rd) return Result.DisjointResult;
        var (od, ops) = IntersectLists(x.Operations, y.Operations);
        if (od) return Result.DisjointResult;
        return Result.Bounded(new NotionScope(x.AccountId, pages, ops));
    }

    private static Result IntersectMcp(McpScope x, McpScope y)
    {
        if (!string.Equals(x.ServerId, y.ServerId, System.StringComparison.Ordinal)
            || !string.Equals(x.CapabilityId, y.CapabilityId, System.StringComparison.Ordinal))
            return Result.DisjointResult;
        if (!string.IsNullOrEmpty(x.SchemaSha256) && !string.IsNullOrEmpty(y.SchemaSha256)
            && !string.Equals(x.SchemaSha256, y.SchemaSha256, System.StringComparison.Ordinal))
            return Result.DisjointResult;
        var tc = string.IsNullOrEmpty(x.TargetConstraints) ? y.TargetConstraints
               : string.IsNullOrEmpty(y.TargetConstraints) ? x.TargetConstraints
               : string.Equals(x.TargetConstraints, y.TargetConstraints, System.StringComparison.Ordinal) ? x.TargetConstraints
               : null;
        if (tc is null) return Result.DisjointResult;
        return Result.Bounded(new McpScope(x.ServerId, x.CapabilityId, x.SchemaSha256 ?? y.SchemaSha256 ?? "", tc));
    }

    private static Result IntersectBuiltin(BuiltinScope x, BuiltinScope y)
    {
        if (!string.Equals(x.CapabilityId, y.CapabilityId, System.StringComparison.Ordinal))
            return Result.DisjointResult;
        var (ed, ents) = IntersectLists(x.EntityIds ?? System.Array.Empty<string>(), y.EntityIds ?? System.Array.Empty<string>());
        if (ed) return Result.DisjointResult;
        return Result.Bounded(new BuiltinScope(x.CapabilityId, ents));
    }

    /// <summary>
    /// Intersects two string lists. An empty list means "unrestricted" and passes through. Two
    /// non-empty, non-overlapping lists are <see cref="Kind.Disjoint"/>.
    /// </summary>
    private static (bool disjoint, IReadOnlyList<string> common) IntersectLists(
        IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count == 0) return (false, b);
        if (b.Count == 0) return (false, a);
        var common = a.Intersect(b, System.StringComparer.Ordinal).ToList();
        if (common.Count == 0) return (true, System.Array.Empty<string>());
        return (false, common);
    }
}
