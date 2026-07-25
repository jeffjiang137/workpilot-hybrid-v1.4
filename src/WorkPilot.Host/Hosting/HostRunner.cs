using System;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Host.Core.Health;
using WorkPilot.Host.Core.Scheduling;

namespace WorkPilot.Host.Hosting;

/// <summary>
/// The background Host process body (T08 "Host 生命周期" + "mutex"). Acquires a global named mutex
/// for single-instance guarding, runs a heartbeat loop (the materialization worker plugs in via
/// <see cref="IHostWorker"/> in T09), and shuts down gracefully on an external stop signal. All
/// OS-specific concerns (mutex, timer) are available on net8.0-windows; this file compiles only on a
/// real Windows build and is delivered as source in this task.
/// </summary>
public sealed class HostRunner : IAsyncDisposable
{
    /// <summary>Heartbeat cadence. A monitor uses this plus a freshness threshold to detect a crashed Host.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    private readonly ITaskScheduler _scheduler;
    private readonly IClock _clock;
    private readonly string _appId;
    private readonly IHostWorker? _worker;
    private readonly CancellationTokenSource _shutdown = new();
    private Mutex? _mutex;
    private Task? _loop;
    private DateTimeOffset? _lastHeartbeatUtc;

    public HostRunner(ITaskScheduler scheduler, IClock clock, string appId, IHostWorker? worker = null)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _appId = appId;
        _worker = worker;
    }

    /// <summary>The global mutex name for this application identity.</summary>
    public string MutexName => global::WorkPilot.Host.Core.Scheduling.MutexName.ForApp(_appId);

    /// <summary>The OS task name this Host is associated with.</summary>
    public string TaskName => HostTaskName.ForApp(_appId);

    /// <summary>Last local heartbeat, or null if the loop never ticked.</summary>
    public DateTimeOffset? LastHeartbeatUtc => _lastHeartbeatUtc;

    /// <summary>
    /// Try to acquire the single-instance mutex. Returns false if another Host instance already
    /// holds it (so this instance should exit without doing work).
    /// </summary>
    public bool TryAcquireSingleInstance()
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            return createdNew;
        }
        catch (UnauthorizedAccessException)
        {
            // Another instance owns the mutex (or ACL prevents access).
            return false;
        }
    }

    public void ReleaseMutex()
    {
        try { _mutex?.ReleaseMutex(); } catch (ApplicationException) { /* released by another thread */ }
        _mutex?.Dispose();
        _mutex = null;
    }

    /// <summary>Start the heartbeat loop. Idempotent.</summary>
    public void Start() => _loop ??= RunLoopAsync(_shutdown.Token);

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                _lastHeartbeatUtc = _clock.UtcNow;
                if (_worker is not null)
                    await _worker.TickAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // graceful shutdown
        }
    }

    /// <summary>Stop the loop and release resources. Safe to await multiple times.</summary>
    public async Task StopAsync()
    {
        if (!_shutdown.IsCancellationRequested)
            _shutdown.Cancel();
        if (_loop is not null)
            await _loop;
    }

    /// <summary>Scheduler-side health for the associated task (T08 "health").</summary>
    public Task<Result<HostHealth>> GetHealthAsync(CancellationToken cancellationToken = default)
        => _scheduler.GetHealthAsync(TaskName, cancellationToken);

    /// <summary>Local health evaluated from the in-memory heartbeat (crash detection).</summary>
    public HostHealth EvaluateLocalHealth(TimeSpan freshThreshold)
        => HostHealthMonitor.Evaluate(_lastHeartbeatUtc, _clock.UtcNow, freshThreshold);

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        ReleaseMutex();
        _shutdown.Dispose();
    }
}
