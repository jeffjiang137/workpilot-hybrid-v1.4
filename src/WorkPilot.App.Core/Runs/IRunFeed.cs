using System;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation.Run;

namespace WorkPilot.App.Core.Runs;

/// <summary>
/// Live run-change subscription source for the run list / detail views (LOG-002 real-time, UI-A07).
/// Implemented by a polling adapter over <see cref="IRunRepository"/> in the host, or an in-memory
/// source for tests. Deliberately dependency-free (no Rx) so <c>WorkPilot.App.Core</c> stays BCL-only.
/// </summary>
public interface IRunFeed
{
    /// <summary>Registers a handler for run-change notifications. Returns a token that unsubscribes on Dispose.</summary>
    IDisposable Subscribe(Action<RunFeedItem> handler);
}

/// <summary>In-memory <see cref="IRunFeed"/> for unit tests and the host dev loop.</summary>
public sealed class InMemoryRunFeed : IRunFeed, IDisposable
{
    private readonly object _gate = new();
    private readonly System.Collections.Generic.List<Action<RunFeedItem>> _handlers = new();
    private readonly System.Collections.Generic.Dictionary<RunId, RunFeedItem> _pending = new();
    private bool _coalescing;

    public IDisposable Subscribe(Action<RunFeedItem> handler)
    {
        if (handler is null) throw new ArgumentNullException(nameof(handler));
        lock (_gate) _handlers.Add(handler);
        return new Subscription(() =>
        {
            lock (_gate) _handlers.Remove(handler);
        });
    }

    /// <summary>Pushes a run-change notification to all subscribers.</summary>
    public void Publish(RunFeedItem item)
    {
        if (item is null) throw new ArgumentNullException(nameof(item));
        if (_coalescing)
        {
            lock (_gate)
            {
                // Coalesce rapid updates for the same run into one merged notification (UI-A07).
                if (_pending.TryGetValue(item.RunId, out var existing))
                {
                    var merged = new System.Collections.Generic.List<RunEvent>(existing.Events);
                    merged.AddRange(item.Events);
                    _pending[item.RunId] = new RunFeedItem(item.RunId, merged, existing.Terminal || item.Terminal);
                }
                else
                {
                    _pending[item.RunId] = item;
                }
            }
            return;
        }

        foreach (var h in SnapshotHandlers())
            h(item);
    }

    /// <summary>Starts buffering publishes; <see cref="Flush"/> emits one merged item per run.</summary>
    public void BeginCoalescing() => _coalescing = true;

    /// <summary>Emits one merged notification per buffered run and stops coalescing (UI-A07 batching).</summary>
    public void Flush()
    {
        System.Collections.Generic.List<RunFeedItem> toEmit;
        lock (_gate)
        {
            toEmit = new System.Collections.Generic.List<RunFeedItem>(_pending.Values);
            _pending.Clear();
            _coalescing = false;
        }
        foreach (var item in toEmit)
            foreach (var h in SnapshotHandlers())
                h(item);
    }

    private System.Collections.Generic.List<Action<RunFeedItem>> SnapshotHandlers()
    {
        lock (_gate) return new System.Collections.Generic.List<Action<RunFeedItem>>(_handlers);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _handlers.Clear();
            _pending.Clear();
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly Action _unsubscribe;
        public Subscription(Action unsubscribe) => _unsubscribe = unsubscribe;
        public void Dispose() => _unsubscribe();
    }
}
