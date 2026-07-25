using WorkPilot.Models;

namespace WorkPilot.Services;

public sealed class AutomationScheduler(AutomationRepository repository, DatabaseService database,
                                        ProjectRepository projects, AgentService agent) : IAsyncDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private Task? _loop;
    public event EventHandler? AutomationChanged;

    public void Start() => _loop ??= RunLoopAsync(_shutdown.Token);

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            await CheckDueAsync(cancellationToken);
            while (await timer.WaitForNextTickAsync(cancellationToken)) await CheckDueAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AppLogger.Info("Automation scheduler stopped");
        }
        catch (Exception error)
        {
            AppLogger.Error("Automation scheduler failed", error);
        }
    }

    private async Task CheckDueAsync(CancellationToken cancellationToken)
    {
        if (!await _runLock.WaitAsync(0, cancellationToken)) return;
        try
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var item in (await repository.GetAllAsync()).Where(x => x.Enabled && x.NextRunAt <= now))
                await ExecuteAsync(item, cancellationToken);
        }
        finally { _runLock.Release(); }
    }

    private async Task ExecuteAsync(Automation item, CancellationToken cancellationToken)
    {
        var next = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(item.IntervalMinutes, 1, 10080));
        try
        {
            var settings = await database.LoadSettingsAsync();
            var project = settings.ActiveProjectId is null ? null : await projects.GetAsync(settings.ActiveProjectId, cancellationToken);
            var spaceId = settings.ActiveSpaceId ?? throw new InvalidOperationException("没有可用空间");
            var conversation = await database.EnsureConversationAsync(spaceId, project?.Id, cancellationToken: cancellationToken);
            var readOnlySettings = settings with { PermissionMode = 1 };
            var progress = new Progress<AgentEvent>(_ => { });
            await agent.RunAsync(new(conversation.Id, item.Prompt, project, readOnlySettings), progress,
                _ => Task.FromResult(false), cancellationToken);
            await repository.SaveAsync(item with { LastRunAt = DateTimeOffset.UtcNow, NextRunAt = next, LastStatus = "完成" });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            AppLogger.Error($"Automation {item.Id} failed", error);
            await repository.SaveAsync(item with { LastRunAt = DateTimeOffset.UtcNow, NextRunAt = next, LastStatus = "失败：" + error.Message });
        }
        AutomationChanged?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        if (_loop is not null) await _loop;
        _shutdown.Dispose();
        _runLock.Dispose();
    }
}
