using WorkPilot.Host.Core.Scheduling;
using Xunit;

namespace WorkPilot.Host.Core.Tests;

public class MutexNameTests
{
    [Fact]
    public void Deterministic_for_same_appId()
        => Assert.Equal(MutexName.ForApp("app1"), MutexName.ForApp("app1"));

    [Fact]
    public void Uses_global_namespace()
        => Assert.StartsWith(@"Global\", MutexName.ForApp("app1"));
}
