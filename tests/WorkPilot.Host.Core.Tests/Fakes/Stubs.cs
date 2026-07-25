using System;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Host.Core.Health;
using WorkPilot.Host.Core.Scheduling;

namespace WorkPilot.Host.Core.Tests.Fakes;

/// <summary>In-memory <see cref="ITaskScheduler"/> that records calls and returns scripted results.</summary>
public sealed class StubTaskScheduler : ITaskScheduler
{
    public int RegisterCallCount;
    public int QueryCallCount;
    public int RemoveCallCount;
    public HostTaskStatus QueryResult = HostTaskStatus.NotFound;
    public HostTaskStatus RegisterResult = HostTaskStatus.Registered;
    public HostTaskDescriptor? LastRegistered;

    public Task<Result<HostTaskStatus>> RegisterAsync(HostTaskDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        RegisterCallCount++;
        LastRegistered = descriptor;
        return Task.FromResult(Result<HostTaskStatus>.Ok(RegisterResult));
    }

    public Task<Result<HostTaskStatus>> QueryAsync(string taskName, CancellationToken cancellationToken = default)
    {
        QueryCallCount++;
        return Task.FromResult(Result<HostTaskStatus>.Ok(QueryResult));
    }

    public Task<Result<bool>> RemoveAsync(string taskName, CancellationToken cancellationToken = default)
    {
        RemoveCallCount++;
        return Task.FromResult(Result<bool>.Ok(true));
    }

    public Task<Result<HostHealth>> GetHealthAsync(string taskName, CancellationToken cancellationToken = default)
        => Task.FromResult(Result<HostHealth>.Ok(HostHealth.Healthy(DateTimeOffset.UtcNow)));
}

/// <summary>Returns a fixed SID.</summary>
public sealed class StubSidResolver : ISidResolver
{
    public string Sid { get; set; } = "S-1-5-21-1000";
    public Task<string> ResolveCurrentUserSidAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Sid);
}

/// <summary>Throws on SID resolution to exercise the failure path.</summary>
public sealed class FailingSidResolver : ISidResolver
{
    public Task<string> ResolveCurrentUserSidAsync(CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("no SID");
}

/// <summary>Fixed clock for deterministic time-based tests.</summary>
public sealed class StubClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public DateTimeOffset Now => UtcNow;
}
