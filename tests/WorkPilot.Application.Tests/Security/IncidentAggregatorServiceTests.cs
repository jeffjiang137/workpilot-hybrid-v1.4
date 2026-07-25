using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Application.Security;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Security;
using WorkPilot.Domain.Security.Detectors;
using Xunit;

namespace WorkPilot.Application.Tests.Security;

public sealed class IncidentAggregatorServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly IIdGenerator Ids = new SequentialIdGenerator();
    private static readonly IClock Clock = new FakeClock(T0);
    private static readonly SourceReference Src = new("connector", "github");

    private static SecurityEvent Ev(SecuritySeverity sev, DateTimeOffset at, string? fp = null) =>
        new(SecurityEventId.Create(Ids), at, SecurityEventType.AuthFailureContinuous, sev,
            fp ?? SecurityEventFingerprint.Compute(SecurityEventType.AuthFailureContinuous, Src, null, null, null),
            Src, null, null, new Dictionary<string, string> { ["n"] = "1" }, DetectorConstants.DetectorVersion);

    private static IncidentAggregatorService NewService(out InMemorySecurityStore store, out RecordingNotificationSink sink)
    {
        store = new InMemorySecurityStore();
        sink = new RecordingNotificationSink();
        return new IncidentAggregatorService(store, store, new SlidingNotificationGate(), sink, Clock, Ids);
    }

    [Fact]
    public async Task Hundred_same_fingerprint_events_produce_one_initial_and_one_escalation_notification()
    {
        var service = NewService(out var store, out var sink);

        await service.ProcessEventAsync(Ev(SecuritySeverity.Low, T0), CancellationToken.None);
        for (var i = 0; i < 99; i++)
            await service.ProcessEventAsync(Ev(SecuritySeverity.High, T0), CancellationToken.None);

        Assert.Equal(100, store.EventCount);
        var incidents = await store.ListAsync(null, 100, CancellationToken.None);
        Assert.Single(incidents);
        Assert.Equal(100, incidents[0].Count);
        // First event → Initial; first High → one Escalation. The other 98 are silenced.
        Assert.Equal(2, sink.Calls);
    }

    [Fact]
    public async Task All_high_events_within_window_only_notify_once()
    {
        var service = NewService(out var store, out var sink);

        for (var i = 0; i < 5; i++)
            await service.ProcessEventAsync(Ev(SecuritySeverity.High, T0), CancellationToken.None);

        Assert.Single(await store.ListAsync(null, 100, CancellationToken.None));
        Assert.Equal(1, sink.Calls); // Initial only, no escalation
    }

    [Fact]
    public async Task Resolved_incident_reopened_notifies_again()
    {
        var service = NewService(out var store, out var sink);

        await service.ProcessEventAsync(Ev(SecuritySeverity.Low, T0), CancellationToken.None);
        var incidents = await store.ListAsync(null, 100, CancellationToken.None);
        var incident = incidents[0];

        Assert.Equal(1, sink.Calls); // Initial delivered

        var resolve = await service.ResolveAsync(incident, IncidentResolutionCode.Remediated, "verified by operator", CancellationToken.None);
        Assert.True(resolve.IsSuccess);

        // A fresh same-fingerprint event after resolution re-opens and alerts again.
        await service.ProcessEventAsync(Ev(SecuritySeverity.High, T0), CancellationToken.None);
        var after = await store.ListAsync(null, 100, CancellationToken.None);
        Assert.Contains(after, i => i.State == IncidentState.Reopened);
        Assert.Equal(2, sink.Calls); // re-open Initial delivered (gate was reset on resolve)
    }

    [Fact]
    public async Task Resolve_rejects_note_over_500_chars()
    {
        var service = NewService(out var store, out _);
        await service.ProcessEventAsync(Ev(SecuritySeverity.Low, T0), CancellationToken.None);
        var incident = (await store.ListAsync(null, 100, CancellationToken.None))[0];

        var longNote = new string('x', 501);
        var r = await service.ResolveAsync(incident, IncidentResolutionCode.Other, longNote, CancellationToken.None);
        Assert.False(r.IsSuccess);
    }

    [Fact]
    public async Task Same_fingerprint_within_window_merges_into_single_incident()
    {
        var service = NewService(out var store, out _);
        var fp = SecurityEventFingerprint.Compute(SecurityEventType.AuthFailureContinuous, Src, null, null, null);
        await service.ProcessEventAsync(Ev(SecuritySeverity.Medium, T0, fp), CancellationToken.None);
        await service.ProcessEventAsync(Ev(SecuritySeverity.Medium, T0, fp), CancellationToken.None);

        var incidents = await store.ListAsync(null, 100, CancellationToken.None);
        Assert.Single(incidents);
        Assert.Equal(2, incidents[0].Count);
    }
}

internal sealed class InMemorySecurityStore : ISecurityEventStore, IIncidentStore
{
    private readonly List<SecurityEvent> _events = new();
    private readonly List<Incident> _incidents = new();

    public int EventCount => _events.Count;

    public Task<Result> AppendAsync(SecurityEvent e, CancellationToken ct)
    {
        _events.Add(e);
        return Task.FromResult(Result.Success());
    }

    public Task<IReadOnlyList<SecurityEvent>> ListRecentAsync(int limit, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<SecurityEvent>>(_events.OrderByDescending(x => x.OccurredAtUtc).Take(limit).ToList());

    public Task<bool> ExistsRecentAsync(string fingerprint, DateTimeOffset since, CancellationToken ct)
        => Task.FromResult(_events.Any(e => e.Fingerprint == fingerprint && e.OccurredAtUtc >= since));

    public Task<Incident?> GetOpenByFingerprintAsync(string fingerprint, DateTimeOffset since, CancellationToken ct)
        => Task.FromResult<Incident?>(_incidents
            .Where(i => i.Fingerprint == fingerprint && i.LastSeenUtc >= since)
            .OrderByDescending(i => i.LastSeenUtc).FirstOrDefault());

    public Task<Incident?> GetByIdAsync(IncidentId id, CancellationToken ct)
        => Task.FromResult<Incident?>(_incidents.FirstOrDefault(i => i.Id == id));

    public Task InsertAsync(Incident incident, CancellationToken ct)
    {
        _incidents.Add(incident);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Incident incident, CancellationToken ct)
    {
        var idx = _incidents.FindIndex(x => x.Id == incident.Id);
        if (idx >= 0) _incidents[idx] = incident;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Incident>> ListAsync(IncidentState? state, int limit, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<Incident>>(_incidents
            .Where(i => state == null || i.State == state)
            .Take(limit).ToList());
}
