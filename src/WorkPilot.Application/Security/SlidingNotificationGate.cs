using System.Collections.Generic;
using System;
using WorkPilot.Domain.Security;

namespace WorkPilot.Application.Security;

/// <summary>
/// In-memory implementation of <see cref="INotificationGate"/>. Enforces doc 06 §3: at most one
/// Initial and at most one Escalation notification per fingerprint, so a burst of same-fingerprint
/// events produces no notification storm. <see cref="Reset"/> clears state when an incident is
/// resolved, allowing a later re-open to alert again.
/// </summary>
public sealed class SlidingNotificationGate : INotificationGate
{
    private readonly object _lock = new();
    private readonly HashSet<string> _initialDelivered = new(StringComparer.Ordinal);
    private readonly HashSet<string> _escalationDelivered = new(StringComparer.Ordinal);

    public bool ShouldDeliver(Incident incident, IncidentNotification notification, DateTimeOffset now)
    {
        lock (_lock)
        {
            var fp = incident.Fingerprint;
            switch (notification.Kind)
            {
                case IncidentNotificationKind.Initial:
                    if (_initialDelivered.Contains(fp)) return false;
                    _initialDelivered.Add(fp);
                    return true;
                case IncidentNotificationKind.Escalation:
                    if (_escalationDelivered.Contains(fp)) return false;
                    _escalationDelivered.Add(fp);
                    return true;
                default:
                    return false;
            }
        }
    }

    public void Reset(string fingerprint)
    {
        lock (_lock)
        {
            _initialDelivered.Remove(fingerprint);
            _escalationDelivered.Remove(fingerprint);
        }
    }
}
