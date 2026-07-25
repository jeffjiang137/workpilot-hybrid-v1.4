using WorkPilot.Host.Core.Scheduling;
using Xunit;

namespace WorkPilot.Host.Core.Tests;

public class HostTaskNameTests
{
    [Fact]
    public void Deterministic_for_same_appId()
        => Assert.Equal(HostTaskName.ForApp("app1"), HostTaskName.ForApp("app1"));

    [Fact]
    public void Sanitizes_invalid_chars()
        => Assert.DoesNotContain("\\", HostTaskName.ForApp("a/b:c"));

    [Fact]
    public void IsOurs_true_for_ours()
        => Assert.True(HostTaskName.IsOurs(HostTaskName.ForApp("app1")));

    [Fact]
    public void IsOurs_false_for_other()
        => Assert.False(HostTaskName.IsOurs("SomeOtherProductTask"));

    [Fact]
    public void Empty_appId_throws()
        => Assert.Throws<ArgumentException>(() => HostTaskName.ForApp(""));
}
