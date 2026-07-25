using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Application.Automation.Run.Executors;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Security;

namespace WorkPilot.Application.Security;

/// <summary>Outcome of aggregating one event into an incident.</summary>
public sealed record IncidentAggregationResult(
    Incident Incident,
    IReadOnlyList<IncidentNotification> DeliveredNotifications);

/// <summary>
/// Persists a security event and folds it into its incident (SEC-102/103). Loads the open/reopened
/// incident for the event's fingerprint within the sliding window, applies the pure
/// <see cref="IncidentAggregator"/>, persists the resulting incident, and delivers notifications
/// through <see cref="INotificationGate"/> (which enforces the silent window). This is the
/// <see cref="ISecurityEventEmitter"/> sink the detector engine writes to.
/// </summary>
public sealed class IncidentAggregatorService
{
    private readonly ISecurityEventStore _events;
    private readonly IIncidentStore _incidents;
    private readonly INotificationGate _gate;
    private readonly INotificationSink? _sink;
    private readonly IClock _clock;
    private readonly IIdGenerator _ids;

    public IncidentAggregatorService(
        ISecurityEventStore events,
        IIncidentStore incidents,
        INotificationGate gate,
        INotificationSink? sink,
        IClock clock,
        IIdGenerator ids)
    {
        _events = events;
        _incidents = incidents;
        _gate = gate;
        _sink = sink;
        _clock = clock;
        _ids = ids;
    }

    public async Task<IncidentAggregationResult> ProcessEventAsync(SecurityEvent e, CancellationToken ct)
    {
        await _events.AppendAsync(e, ct);

        var since = e.OccurredAtUtc - IncidentAggregator.DefaultWindow;
        var existing = await _incidents.GetOpenByFingerprintAsync(e.Fingerprint, since, ct);
        var decision = IncidentAggregator.Decide(existing, e, e.OccurredAtUtc, IncidentId.Create(_ids));

        if (decision.IsNew)
            await _incidents.InsertAsync(decision.Incident, ct);
        else
            await _incidents.UpdateAsync(decision.Incident, ct);

        // On resolution the gate state must be cleared so a future re-open can alert again.
        if (decision.Incident.State == IncidentState.Resolved)
            _gate.Reset(decision.Incident.Fingerprint);

        var delivered = new List<IncidentNotification>();
        foreach (var n in decision.Notifications)
        {
            if (!_gate.ShouldDeliver(decision.Incident, n, e.OccurredAtUtc))
                continue;
            await DeliverAsync(n, decision.Incident, ct);
            delivered.Add(n);
        }

        return new IncidentAggregationResult(decision.Incident, delivered);
    }

    /// <summary>SEC-103: resolve an incident. Records the resolution code/note and clears the
    /// notification gate so a future re-open of the same fingerprint can alert again.</summary>
    public async Task<Result> ResolveAsync(Incident incident, IncidentResolutionCode code, string? note, CancellationToken ct)
    {
        if (note is not null && note.Length > 500)
            return Result.Failure(SecurityErrors.IncidentNoteTooLongError(note.Length));
        if (incident.State == IncidentState.Resolved)
            return Result.Success();

        var resolved = incident with
        {
            State = IncidentState.Resolved,
            ResolutionCode = code.ToString(),
            ResolutionNote = note,
            ResolvedAtUtc = _clock.UtcNow,
            UpdatedAtUtc = _clock.UtcNow
        };
        await _incidents.UpdateAsync(resolved, ct);
        _gate.Reset(resolved.Fingerprint);
        return Result.Success();
    }

    private async Task DeliverAsync(IncidentNotification n, Incident incident, CancellationToken ct)
    {
        if (_sink is null) return;
        var content = new NotificationContent(
            Title: $"安全事件 [{incident.Severity}] {incident.Type}",
            Body: $"事件已聚合（累计 {incident.Count} 次），状态 {incident.State}，通知类型 {n.Kind}。");
        try
        {
            await _sink.ShowAsync(content, ct);
        }
        catch
        {
            // Notification delivery must never fail the aggregation (doc 06 §10).
        }
    }
}
