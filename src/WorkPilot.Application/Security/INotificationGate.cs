using System;
using WorkPilot.Domain.Security;

namespace WorkPilot.Application.Security;

/// <summary>
/// Decides whether a notification should actually be delivered, enforcing the silent window
/// (doc 06 §3): the first event of an incident is delivered immediately; an escalation to
/// High/Critical may break the silence <b>once</b> per fingerprint. Without this, a burst of
/// same-fingerprint events would storm the user with toasts.
/// </summary>
public interface INotificationGate
{
    /// <summary>Returns true if this notification may be delivered now (and records the delivery).</summary>
    bool ShouldDeliver(Incident incident, IncidentNotification notification, DateTimeOffset now);

    /// <summary>Clears delivered-state for a fingerprint (called when its incident is resolved).</summary>
    void Reset(string fingerprint);
}
