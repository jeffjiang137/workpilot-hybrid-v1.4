namespace WorkPilot.Services;

public sealed class BoundedLruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _maxEntries;
    private readonly long _maxBytes;
    private readonly TimeSpan _timeToLive;
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _map = [];
    private readonly LinkedList<Entry> _lru = [];
    private readonly object _gate = new();
    private long _bytes;

    public BoundedLruCache(int maxEntries, long maxBytes, TimeSpan timeToLive)
    {
        _maxEntries = maxEntries; _maxBytes = maxBytes; _timeToLive = timeToLive;
    }

    public bool TryGet(TKey key, out TValue? value)
    {
        lock (_gate)
        {
            if (!_map.TryGetValue(key, out var node)) { value = default; return false; }
            if (DateTimeOffset.UtcNow - node.Value.CreatedAt > _timeToLive)
            {
                Remove(node); value = default; return false;
            }
            _lru.Remove(node); _lru.AddFirst(node); value = node.Value.Value; return true;
        }
    }

    public void Set(TKey key, TValue value, long estimatedBytes)
    {
        estimatedBytes = Math.Max(0, estimatedBytes);
        lock (_gate)
        {
            if (_map.Remove(key, out var existing)) { _lru.Remove(existing); _bytes -= existing.Value.Bytes; }
            var node = new LinkedListNode<Entry>(new(key, value, estimatedBytes, DateTimeOffset.UtcNow));
            _map[key] = node; _lru.AddFirst(node); _bytes += estimatedBytes;
            while (_map.Count > _maxEntries || _bytes > _maxBytes)
            {
                if (_lru.Last is null) break; Remove(_lru.Last);
            }
        }
    }

    public void Clear()
    {
        lock (_gate) { _map.Clear(); _lru.Clear(); _bytes = 0; }
    }

    private void Remove(LinkedListNode<Entry> node)
    {
        _lru.Remove(node); _map.Remove(node.Value.Key); _bytes -= node.Value.Bytes;
    }

    private sealed record Entry(TKey Key, TValue Value, long Bytes, DateTimeOffset CreatedAt);
}
