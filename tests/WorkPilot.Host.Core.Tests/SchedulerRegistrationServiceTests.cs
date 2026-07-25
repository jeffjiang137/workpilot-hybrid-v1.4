using System;
using System.IO;
using System.Threading.Tasks;
using WorkPilot.Host.Core.Scheduling;
using WorkPilot.Host.Core.Tests.Fakes;
using Xunit;

namespace WorkPilot.Host.Core.Tests;

public class SchedulerRegistrationServiceTests
{
    private static SchedulerRegistrationService Make(string root, out StubTaskScheduler sched, out string exe)
    {
        exe = Path.Combine(root, "WorkPilot.Host.exe");
        File.WriteAllText(exe, "");
        sched = new StubTaskScheduler();
        return new SchedulerRegistrationService(sched, new StubSidResolver(), new ExecutablePathValidator(), "app1", root);
    }

    [Fact]
    public async Task First_register_creates_and_calls_os_once()
    {
        var root = TempRoot();
        var svc = Make(root, out var sched, out var exe);
        sched.QueryResult = HostTaskStatus.NotFound;

        var result = await svc.RegisterAsync(exe);

        Assert.True(result.IsSuccess);
        Assert.Equal(RegistrationOutcome.Created, result.Value!.Outcome);
        Assert.Equal(1, sched.RegisterCallCount);
        Assert.Equal("S-1-5-21-1000", sched.LastRegistered!.UserId);
        Assert.Single(sched.LastRegistered!.AllowedSids);
        Assert.Equal("S-1-5-21-1000", sched.LastRegistered!.AllowedSids[0]);
        Assert.False(string.IsNullOrEmpty(sched.LastRegistered!.SecurityDescriptorSddl));
    }

    [Fact]
    public async Task Second_register_is_idempotent_and_skips_os()
    {
        var root = TempRoot();
        var svc = Make(root, out var sched, out var exe);
        sched.QueryResult = HostTaskStatus.Registered;

        var result = await svc.RegisterAsync(exe);

        Assert.True(result.IsSuccess);
        Assert.Equal(RegistrationOutcome.AlreadyRegistered, result.Value!.Outcome);
        Assert.Equal(0, sched.RegisterCallCount); // already registered -> no redundant COM call
    }

    [Fact]
    public async Task Tampered_path_fails_with_path_error()
    {
        var root = TempRoot();
        Make(root, out _, out _);
        var svc = new SchedulerRegistrationService(
            new StubTaskScheduler(), new StubSidResolver(), new ExecutablePathValidator(), "app1", root);

        var result = await svc.RegisterAsync(Path.Combine(root, "evil.exe"));

        Assert.False(result.IsSuccess);
        Assert.Equal("SCHREG_PATH", result.Error!.Code);
    }

    [Fact]
    public async Task Sid_resolution_failure_fails()
    {
        var root = TempRoot();
        var exe = Path.Combine(root, "WorkPilot.Host.exe");
        File.WriteAllText(exe, "");
        var svc = new SchedulerRegistrationService(
            new StubTaskScheduler(), new FailingSidResolver(), new ExecutablePathValidator(), "app1", root);

        var result = await svc.RegisterAsync(exe);

        Assert.False(result.IsSuccess);
        Assert.Equal("SCHREG_SID", result.Error!.Code);
    }

    [Fact]
    public async Task Remove_passthrough_calls_os_once()
    {
        var root = TempRoot();
        var svc = Make(root, out var sched, out _);

        var result = await svc.RemoveAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(1, sched.RemoveCallCount);
    }

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "wp_host_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
