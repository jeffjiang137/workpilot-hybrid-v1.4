using System.Text.Json;
using System.Threading.Channels;
using WorkPilot.Models;

namespace WorkPilot.Services;

public sealed class AssetIndexCoordinator : IAsyncDisposable
{
    private const long DatabaseHardLimit = 1_073_741_824;
    private readonly AssetRepository _assets;
    private readonly DatabaseService _database;
    private readonly INativeWorkspaceFactory _native;
    private readonly Channel<ScanRequest> _queue = Channel.CreateBounded<ScanRequest>(new BoundedChannelOptions(200)
        { FullMode = BoundedChannelFullMode.Wait, SingleWriter = false, SingleReader = false });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task[] _workers;
    private readonly Dictionary<string, FileSystemWatcher> _watchers = [];
    private readonly Dictionary<string, CancellationTokenSource> _active = [];
    private readonly Dictionary<string, Timer> _debounceTimers = [];
    private readonly Dictionary<string, DateTimeOffset> _watcherFirstEvent = [];
    private readonly object _watcherGate = new();
    public event EventHandler<IndexState>? ProgressChanged;

    public AssetIndexCoordinator(DatabaseService database, AssetRepository assets, INativeWorkspaceFactory native)
    {
        _database = database; _assets = assets; _native = native;
        _workers = [WorkerLoopAsync(_shutdown.Token), WorkerLoopAsync(_shutdown.Token)];
    }

    public async Task RequestFullScanAsync(Project project, CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _queue.Writer.WriteAsync(new(project, completion), cancellationToken);
        await completion.Task.WaitAsync(cancellationToken);
    }

    public async Task QueueFullScanAsync(Project project, CancellationToken cancellationToken = default) =>
        await _queue.Writer.WriteAsync(new(project, null), cancellationToken);

    public void RemoveProject(string projectId)
    {
        lock (_watcherGate)
        {
            if (_watchers.Remove(projectId, out var watcher)) watcher.Dispose();
            if (_debounceTimers.Remove(projectId, out var timer)) timer.Dispose();
            _watcherFirstEvent.Remove(projectId);
            if (_active.TryGetValue(projectId, out var active)) active.Cancel();
        }
    }

    public async Task PauseAsync(string projectId, CancellationToken cancellationToken = default)
    {
        lock (_watcherGate) { if (_active.TryGetValue(projectId, out var active)) active.Cancel(); }
        await _assets.PauseAsync(projectId, null, cancellationToken);
    }

    public async Task<IndexState?> GetStateAsync(string projectId, CancellationToken cancellationToken = default) =>
        await _assets.GetStateAsync(projectId, cancellationToken);

    private async Task WorkerLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var request in _queue.Reader.ReadAllAsync(cancellationToken))
            {
                CancellationTokenSource projectCancellation;
                lock (_watcherGate)
                {
                    if (_active.ContainsKey(request.Project.Id))
                    {
                        request.Completion?.SetException(new InvalidOperationException("该项目已有索引任务正在运行"));
                        continue;
                    }
                    projectCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    _active[request.Project.Id] = projectCancellation;
                }
                try { await ScanAsync(request.Project, projectCancellation.Token); request.Completion?.SetResult(); }
                catch (OperationCanceledException)
                {
                    await _assets.PauseAsync(request.Project.Id, null, CancellationToken.None);
                    request.Completion?.SetCanceled(projectCancellation.Token);
                }
                catch (Exception error)
                {
                    AppLogger.Error($"Index scan failed for project {request.Project.Id}", error);
                    await _assets.PauseAsync(request.Project.Id, "索引失败，请检查工作区权限后重试", CancellationToken.None);
                    request.Completion?.SetException(error);
                }
                finally
                {
                    lock (_watcherGate) { _active.Remove(request.Project.Id); }
                    projectCancellation.Dispose();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task ScanAsync(Project project, CancellationToken cancellationToken)
    {
        await _database.EnsureSafeIndexRuntimeAsync(cancellationToken);
        if (new FileInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WorkPilot", "workpilot.db")).Length >= DatabaseHardLimit)
            throw new InvalidOperationException("索引数据库达到 1 GiB 上限，已停止新增正文索引");
        var generation = await _assets.BeginGenerationAsync(project.Id, cancellationToken);
        using var workspace = _native.Open(project.WorkspacePath);
        using var scan = workspace.BeginScan(project.IncludeHidden, project.IgnoreRules);
        var counters = new IndexCounters(0, 0, 0, 0, 0); var lastPath = ""; var limit = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await Task.Run(() => scan.Next(), cancellationToken); limit |= page.LimitReached;
            counters = counters with { Discovered = page.FilesSeen };
            foreach (var item in page.Items)
            {
                lastPath = item.RelativePath;
                try { counters = await ProcessItemAsync(project, workspace, item, generation, counters, cancellationToken); }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    await _assets.TouchAsync(project.Id, item.PathKey, generation, cancellationToken);
                    counters = counters with { Processed = counters.Processed + 1, Errors = counters.Errors + 1 };
                }
            }
            await _assets.UpdateProgressAsync(project.Id, generation, counters, lastPath, cancellationToken);
            await PublishAsync(project.Id, cancellationToken);
            if (page.Done || page.Cancelled) break;
        }
        await _assets.CompleteAsync(project.Id, generation, limit, counters, cancellationToken);
        EnsureWatcher(project); await PublishAsync(project.Id, cancellationToken);
    }

    private async Task<IndexCounters> ProcessItemAsync(Project project, INativeWorkspaceSession workspace,
        ScanItem item, long generation, IndexCounters counters, CancellationToken cancellationToken)
    {
        using var fingerprintDoc = JsonDocument.Parse(await Task.Run(() => workspace.QuickFingerprint(item.RelativePath), cancellationToken));
        var fingerprint = fingerprintDoc.RootElement.GetProperty("quick_fingerprint").GetString()!;
        if (!fingerprintDoc.RootElement.GetProperty("stable").GetBoolean())
            throw new InvalidDataException("文件读取期间发生变化");
        if (await _assets.GetFingerprintAsync(project.Id, item.PathKey, cancellationToken) == fingerprint)
        {
            await _assets.TouchAsync(project.Id, item.PathKey, generation, cancellationToken);
            return counters with { Processed = counters.Processed + 1, Skipped = counters.Skipped + 1 };
        }
        var status = "metadata_only_type"; string? sha = null; IReadOnlyList<TextChunk> chunks = []; var replaceChunks = true;
        if (item.SizeBytes > IndexPolicyV13.MaxIndexTextBytes) status = "metadata_only_size_limit";
        else if (AssetTypePolicy.SupportsText(item.FileName, item.Extension))
        {
            try
            {
                using var readDoc = JsonDocument.Parse(await Task.Run(() => workspace.ReadText(item.RelativePath), cancellationToken));
                var content = readDoc.RootElement.GetProperty("content").GetString() ?? "";
                sha = readDoc.RootElement.GetProperty("sha256").GetString();
                try { chunks = TextChunker.Chunk(content, item.FileName, item.RelativePath); status = "indexed"; }
                catch (InvalidDataException) { status = "read_error"; replaceChunks = false; }
            }
            catch (NativeWorkspaceException error) when (error.Code == "UNSUPPORTED_ENCODING")
                { status = "unsupported_encoding"; }
            catch (NativeWorkspaceException) { status = "read_error"; }
        }
        await _assets.UpsertAsync(project, item, fingerprint, status, sha, chunks, generation, replaceChunks, cancellationToken);
        return counters with { Processed = counters.Processed + 1,
            Indexed = counters.Indexed + (status == "indexed" ? 1 : 0),
            Skipped = counters.Skipped + (status == "indexed" ? 0 : 1) };
    }

    private async Task PublishAsync(string projectId, CancellationToken cancellationToken)
    {
        var state = await _assets.GetStateAsync(projectId, cancellationToken);
        if (state is not null) ProgressChanged?.Invoke(this, state);
    }

    private void EnsureWatcher(Project project)
    {
        lock (_watcherGate)
        {
            if (_watchers.Remove(project.Id, out var old)) old.Dispose();
            var watcher = new FileSystemWatcher(project.WorkspacePath)
            {
                IncludeSubdirectories = true, InternalBufferSize = 32 * 1024, EnableRaisingEvents = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            FileSystemEventHandler changed = (_, _) => QueueWatcherScan(project);
            RenamedEventHandler renamed = (_, _) => QueueWatcherScan(project);
            ErrorEventHandler error = (_, _) => QueueWatcherScan(project);
            watcher.Created += changed; watcher.Changed += changed; watcher.Deleted += changed;
            watcher.Renamed += renamed; watcher.Error += error; _watchers[project.Id] = watcher;
        }
    }

    private void QueueWatcherScan(Project project)
    {
        lock (_watcherGate)
        {
            if (_debounceTimers.Remove(project.Id, out var old)) old.Dispose();
            var now = DateTimeOffset.UtcNow;
            if (!_watcherFirstEvent.TryGetValue(project.Id, out var first))
                _watcherFirstEvent[project.Id] = first = now;
            var remaining = Math.Max(1, 2000 - (int)(now - first).TotalMilliseconds);
            var due = Math.Min(500, remaining);
            _debounceTimers[project.Id] = new Timer(_ =>
            {
                _queue.Writer.TryWrite(new(project, null));
                lock (_watcherGate)
                {
                    if (_debounceTimers.Remove(project.Id, out var timer)) timer.Dispose();
                    _watcherFirstEvent.Remove(project.Id);
                }
            }, null, due, Timeout.Infinite);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete(); _shutdown.Cancel();
        try { await Task.WhenAll(_workers).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { AppLogger.Info("Index shutdown cancellation observed"); }
        catch (TimeoutException) { AppLogger.Info("Index workers did not finish within shutdown budget"); }
        lock (_watcherGate)
        {
            foreach (var watcher in _watchers.Values) watcher.Dispose(); _watchers.Clear();
            foreach (var timer in _debounceTimers.Values) timer.Dispose(); _debounceTimers.Clear();
            _watcherFirstEvent.Clear();
            foreach (var active in _active.Values) active.Dispose(); _active.Clear();
        }
        _shutdown.Dispose();
    }

    private sealed record ScanRequest(Project Project, TaskCompletionSource? Completion);
}
