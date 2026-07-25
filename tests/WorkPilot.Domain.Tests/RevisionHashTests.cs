using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation;
using Xunit;

namespace WorkPilot.Domain.Tests;

public class RevisionHashTests
{
    private static readonly AutomationId Auto = AutomationId.Parse("auto_1");

    [Fact]
    public void Create_produces_64_hex_canonical_hash()
    {
        var (rev, _) = Samples.MakeRevision(Auto, 1);
        Assert.Equal(64, rev.CanonicalSha256.Length);
        Assert.All(rev.CanonicalSha256, c => Assert.True(char.IsDigit(c) || (c >= 'a' && c <= 'f')));
    }

    [Fact]
    public void Identical_content_yields_identical_hash()
    {
        var (a, _) = Samples.MakeRevision(Auto, 1);
        var (b, _) = Samples.MakeRevision(Auto, 1);
        Assert.Equal(a.CanonicalSha256, b.CanonicalSha256);
    }

    [Fact]
    public void Different_budget_yields_different_hash()
    {
        var (a, _) = Samples.MakeRevision(Auto, 1, Samples.Budget(maxTokens: 100_000));
        var (b, _) = Samples.MakeRevision(Auto, 1, Samples.Budget(maxTokens: 200_000));
        Assert.NotEqual(a.CanonicalSha256, b.CanonicalSha256);
    }

    [Fact]
    public void Different_permission_scope_yields_different_hash()
    {
        var (a, _) = Samples.MakeRevision(Auto, 1, scope: "read-only");
        var (b, _) = Samples.MakeRevision(Auto, 1, scope: "read-write");
        Assert.NotEqual(a.CanonicalSha256, b.CanonicalSha256);
    }
}
