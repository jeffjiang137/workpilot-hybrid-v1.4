using System;
using System.Collections.Generic;
using WorkPilot.Domain.PermissionGovernance.Evaluation;

namespace WorkPilot.Application.Permission.Policy;

/// <summary>
/// Bounded, TTL-based decision cache (doc 07 §13). Key = policy hash + capability hash + context
/// invariant hash; any policy/save/grant/revoke/source/schema/epoch change broadcasts
/// <see cref="InvalidateAll"/>. The final Current-State Check (doc 07 §11) never trusts the cache —
/// its window is at most 1s, far below the TTL, and the gate re-validates invariants on every call.
/// Bound by entry count (256) to keep memory bounded; TTL default 5 minutes.
/// </summary>
public sealed class PolicyEvaluationCache
{
    private struct Entry
    {
        public PermissionDecision Decision;
        public DateTimeOffset ExpiresAt;
        public int Sequence;
    }

    private readonly Dictionary<string, Entry> _map = new();
    private readonly object _gate = new();
    private readonly TimeSpan _ttl;
    private readonly int _maxEntries;
    private int _seq;

    public PolicyEvaluationCache(TimeSpan? ttl = null, int maxEntries = 256)
    {
        _ttl = ttl ?? TimeSpan.FromMinutes(5);
        _maxEntries = maxEntries;
    }

    public bool TryGet(string key, DateTimeOffset now, out PermissionDecision decision)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var entry))
            {
                if (entry.ExpiresAt > now)
                {
                    decision = entry.Decision;
                    return true;
                }
                _map.Remove(key);
            }
            decision = null!;
            return false;
        }
    }

    public void Set(string key, PermissionDecision decision, DateTimeOffset now)
    {
        lock (_gate)
        {
            _map[key] = new Entry { Decision = decision, ExpiresAt = now.Add(_ttl), Sequence = ++_seq };
            if (_map.Count > _maxEntries)
                EvictOldest();
        }
    }

    /// <summary>Broadcast on any policy mutation. Clears all cached decisions (doc 07 §13).</summary>
    public void InvalidateAll()
    {
        lock (_gate)
            _map.Clear();
    }

    private void EvictOldest()
    {
        // Remove the entry with the smallest sequence (least-recently-set).
        string? oldestKey = null;
        var oldestSeq = int.MaxValue;
        foreach (var kvp in _map)
        {
            if (kvp.Value.Sequence < oldestSeq)
            {
                oldestSeq = kvp.Value.Sequence;
                oldestKey = kvp.Key;
            }
        }
        if (oldestKey is not null)
            _map.Remove(oldestKey);
    }
}
