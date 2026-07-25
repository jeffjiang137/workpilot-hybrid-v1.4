using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Application.Automation;
using WorkPilot.Application.Permission.Policy;
using WorkPilot.Application.Security;
using WorkPilot.Application.Security.Governance;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation;
using WorkPilot.Domain.PermissionGovernance;
using WorkPilot.Domain.Security;
using WorkPilot.Domain.Security.Audit;
using Xunit;

namespace WorkPilot.Application.Tests.Security.Governance;

public sealed class GovernanceCoordinatorTests
{
    private static readonly IClock Clock = new FakeClock(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
    private static readonly IIdGenerator Ids = new SequentialIdGenerator();

    private static string Hash64(string input)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }

    private static PolicyGrant MakeGrant(PolicyGrantId id)
    {
        var scope = new LocalProjectScope("proj-1", new List<string> { "src" }, new List<string> { "read" });
        return new PolicyGrant(
            id, "auto-1", "rev-1", "space-1", "exp-1",
            "mcp", "src-1", "cap-1", "sha-cap",
            scope, PolicyGrant.ComputeScopeSha256(scope),
            RiskLevel.Medium,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(7),
            1, DateTimeOffset.UtcNow, null);
    }

    private static Incident MakeIncident(IncidentId id, IncidentState state) =>
        new(id, Hash64("fp-" + id.Value), state, SecuritySeverity.High, SecurityEventType.AuthFailureContinuous,
            DateTimeOffset.UtcNow.AddMinutes(-3), DateTimeOffset.UtcNow, 1,
            new List<string> { Hash64("e1") }, null, null, null,
            DateTimeOffset.UtcNow.AddMinutes(-3), DateTimeOffset.UtcNow, null);

    // ---- GrantGovernanceService: Impact changed (doc 06 §10) ----

    [Fact]
    public async Task Grant_revoke_with_matching_preview_token_succeeds()
    {
        var grants = new InMemoryGrantStore();
        var epoch = new InMemoryRevocationEpoch();
        var audit = new InMemoryAuditLogStore();
        var svc = new GrantGovernanceService(grants, epoch, Clock, new AuditLogWriter(audit, new StaticAuditKeyProvider(), Clock));

        var id = PolicyGrantId.Create(Ids);
        grants.Seed(MakeGrant(id));

        var preview = await svc.PreviewRevokeAsync(id, CancellationToken.None);
        Assert.True(preview.IsSuccess);

        var revoke = await svc.RevokeAsync(id, preview.Value!.ImpactToken, CancellationToken.None);
        Assert.True(revoke.IsSuccess);
        Assert.Equal(2L, epoch.Current); // bumped once
        Assert.NotNull((await grants.GetAsync(id, CancellationToken.None)).Value!.RevokedAtUtc);
    }

    [Fact]
    public async Task Grant_revoke_with_stale_token_after_epoch_change_is_refused()
    {
        var grants = new InMemoryGrantStore();
        var epoch = new InMemoryRevocationEpoch();
        var audit = new InMemoryAuditLogStore();
        var svc = new GrantGovernanceService(grants, epoch, Clock, new AuditLogWriter(audit, new StaticAuditKeyProvider(), Clock));

        var id = PolicyGrantId.Create(Ids);
        grants.Seed(MakeGrant(id));

        var preview = await svc.PreviewRevokeAsync(id, CancellationToken.None);
        epoch.Bump(); // external change between preview and apply

        var revoke = await svc.RevokeAsync(id, preview.Value!.ImpactToken, CancellationToken.None);
        Assert.False(revoke.IsSuccess);
        Assert.Equal("SEC_GOV_IMPACT_CHANGED", revoke.Error!.Code);
    }

    // ---- SourceGovernanceService: partial failure (doc 06 §10) ----

    [Fact]
    public async Task Disable_source_all_subactions_succeed_and_epoch_bumps()
    {
        var backend = new ScriptedSourceBackend();
        var epoch = new InMemoryRevocationEpoch();
        var svc = new SourceGovernanceService(backend, epoch);

        var result = await svc.DisableSourceAsync("mcp", "src-1", CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.Equal(2L, epoch.Current);
        Assert.Contains("set:mcp:src-1:False", backend.Actions);
        Assert.Contains("terminate:mcp:src-1", backend.Actions);
    }

    [Fact]
    public async Task Disable_source_partial_failure_reports_subactions_but_epoch_still_bumps()
    {
        var backend = new ScriptedSourceBackend { TerminateFails = true };
        var epoch = new InMemoryRevocationEpoch();
        var svc = new SourceGovernanceService(backend, epoch);

        var result = await svc.DisableSourceAsync("mcp", "src-1", CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal("SEC_GOV_PARTIAL_FAILURE", result.Error!.Code);
        Assert.Equal(2L, epoch.Current); // defense-in-depth: still invalidated
    }

    // ---- EmergencyStopCoordinator (doc 06 §6.4) ----

    [Fact]
    public async Task Emergency_stop_pauses_enabled_automations_sets_flag_and_bumps_epoch()
    {
        var state = new InMemorySecurityStateStore();
        var epoch = new InMemoryRevocationEpoch();
        var automations = new InMemoryAutomationRepository();
        var a1 = EnabledDef("a1");
        var a2 = EnabledDef("a2");
        var a3 = EnabledDef("a3");
        automations.Defs.Add(a1); automations.Defs.Add(a2); automations.Defs.Add(a3);

        var coordinator = new EmergencyStopCoordinator(state, epoch, automations, new AuditLogWriter(new InMemoryAuditLogStore(), new StaticAuditKeyProvider(), Clock));
        var result = await coordinator.StopAsync("operator", CancellationToken.None);
        Assert.True(result.IsSuccess);

        Assert.Equal("true", (await state.GetAsync("emergency_stop", CancellationToken.None)).Value);
        Assert.Equal(2L, epoch.Current);
        Assert.All(automations.Defs, d => Assert.Equal(AutomationLifecycle.Paused, d.Lifecycle));
    }

    private static AutomationDefinition EnabledDef(string name)
    {
        return new AutomationDefinition(
            AutomationId.Create(Ids), SpaceId.Create(Ids), name, "",
            AutomationLifecycle.Enabled, AutomationRevisionId.Create(Ids), 1, 0,
            Clock.UtcNow, Clock.UtcNow);
    }

    [Fact]
    public async Task Emergency_stop_twice_is_refused()
    {
        var state = new InMemorySecurityStateStore();
        var coordinator = new EmergencyStopCoordinator(state, new InMemoryRevocationEpoch(), new InMemoryAutomationRepository(),
            new AuditLogWriter(new InMemoryAuditLogStore(), new StaticAuditKeyProvider(), Clock));
        Assert.True((await coordinator.StopAsync("operator", CancellationToken.None)).IsSuccess);
        var second = await coordinator.StopAsync("operator", CancellationToken.None);
        Assert.False(second.IsSuccess);
        Assert.Equal("SEC_GOV_EMERGENCY_STOP_ACTIVE", second.Error!.Code);
    }

    [Fact]
    public async Task Emergency_resume_clears_flag()
    {
        var state = new InMemorySecurityStateStore();
        var coordinator = new EmergencyStopCoordinator(state, new InMemoryRevocationEpoch(), new InMemoryAutomationRepository(),
            new AuditLogWriter(new InMemoryAuditLogStore(), new StaticAuditKeyProvider(), Clock));
        await coordinator.StopAsync("operator", CancellationToken.None);
        Assert.True((await coordinator.ResumeAsync("operator", CancellationToken.None)).IsSuccess);
        Assert.Equal("false", (await state.GetAsync("emergency_stop", CancellationToken.None)).Value);
    }

    // ---- IncidentGovernanceService (doc 06 §3) ----

    [Fact]
    public async Task Incident_acknowledge_open_to_acknowledged()
    {
        var store = new InMemoryIncidentStore();
        var id = IncidentId.Create(Ids);
        store.Seed(MakeIncident(id, IncidentState.Open));
        var svc = new IncidentGovernanceService(store, BuildAggregator(store), Clock);

        Assert.True((await svc.AcknowledgeAsync(id, CancellationToken.None)).IsSuccess);
        Assert.Equal(IncidentState.Acknowledged, (await store.GetByIdAsync(id, CancellationToken.None))!.State);
    }

    [Fact]
    public async Task Incident_resolve_marks_resolved_via_aggregator()
    {
        var store = new InMemoryIncidentStore();
        var id = IncidentId.Create(Ids);
        store.Seed(MakeIncident(id, IncidentState.Open));
        var svc = new IncidentGovernanceService(store, BuildAggregator(store), Clock);

        var result = await svc.ResolveAsync(id, IncidentResolutionCode.Remediated, "fixed", CancellationToken.None);
        Assert.True(result.IsSuccess);
        var resolved = await store.GetByIdAsync(id, CancellationToken.None);
        Assert.Equal(IncidentState.Resolved, resolved!.State);
        Assert.Equal("fixed", resolved.ResolutionNote);
    }

    private static IncidentAggregatorService BuildAggregator(IIncidentStore incidents) =>
        new(new InMemorySecurityEventStore(), incidents, new SlidingNotificationGate(), null, Clock, Ids);
}

// ---- in-memory port doubles ----

internal sealed class InMemoryIncidentStore : IIncidentStore
{
    private readonly Dictionary<IncidentId, Incident> _byId = new();
    private readonly Dictionary<string, List<Incident>> _byFp = new();

    public void Seed(Incident i)
    {
        _byId[i.Id] = i;
        if (!_byFp.TryGetValue(i.Fingerprint, out var list)) { list = new(); _byFp[i.Fingerprint] = list; }
        list.Add(i);
    }

    public Task<Result> AppendAsync(SecurityEvent e, CancellationToken ct) => Task.FromResult(Result.Success());
    public Task<IReadOnlyList<SecurityEvent>> ListRecentAsync(int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<SecurityEvent>>(new List<SecurityEvent>());
    public Task<bool> ExistsRecentAsync(string fp, DateTimeOffset since, CancellationToken ct) => Task.FromResult(false);

    public Task<Incident?> GetOpenByFingerprintAsync(string fp, DateTimeOffset since, CancellationToken ct)
    {
        if (_byFp.TryGetValue(fp, out var list))
        {
            var recent = list.Where(i => i.LastSeenUtc >= since).OrderByDescending(i => i.LastSeenUtc).FirstOrDefault();
            return Task.FromResult(recent);
        }
        return Task.FromResult<Incident?>(null);
    }

    public Task<Incident?> GetByIdAsync(IncidentId id, CancellationToken ct) => Task.FromResult(_byId.TryGetValue(id, out var i) ? i : null);

    public Task InsertAsync(Incident incident, CancellationToken ct)
    {
        _byId[incident.Id] = incident;
        if (!_byFp.TryGetValue(incident.Fingerprint, out var list)) { list = new(); _byFp[incident.Fingerprint] = list; }
        list.Add(incident);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Incident incident, CancellationToken ct)
    {
        _byId[incident.Id] = incident;
        if (_byFp.TryGetValue(incident.Fingerprint, out var list))
        {
            var idx = list.FindIndex(x => x.Id == incident.Id);
            if (idx >= 0) list[idx] = incident;
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Incident>> ListAsync(IncidentState? state, int limit, CancellationToken ct)
    {
        var q = _byId.Values.AsEnumerable();
        if (state is not null) q = q.Where(i => i.State == state);
        return Task.FromResult<IReadOnlyList<Incident>>(q.Take(limit).ToList());
    }
}

internal sealed class InMemorySecurityEventStore : ISecurityEventStore
{
    public List<SecurityEvent> Events { get; } = new();
    public Task<Result> AppendAsync(SecurityEvent e, CancellationToken ct) { Events.Add(e); return Task.FromResult(Result.Success()); }
    public Task<IReadOnlyList<SecurityEvent>> ListRecentAsync(int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<SecurityEvent>>(Events.Take(limit).ToList());
    public Task<bool> ExistsRecentAsync(string fp, DateTimeOffset since, CancellationToken ct) => Task.FromResult(Events.Any(e => e.Fingerprint == fp && e.OccurredAtUtc >= since));
}

internal sealed class InMemorySecurityStateStore : ISecurityStateStore
{
    private readonly Dictionary<string, string> _data = new();
    public Task<Result> SetAsync(string key, string value, CancellationToken ct) { _data[key] = value; return Task.FromResult(Result.Success()); }
    public Task<Result<string?>> GetAsync(string key, CancellationToken ct) =>
        Task.FromResult(Result<string?>.Ok(_data.TryGetValue(key, out var v) ? v : null));
}

internal sealed class ScriptedSourceBackend : ISourceGovernanceBackend
{
    public bool TerminateFails;
    public List<string> Actions { get; } = new();

    public Task<Result> SetSourceEnabledAsync(string kind, string id, bool enabled, CancellationToken ct)
    {
        Actions.Add($"set:{kind}:{id}:{enabled}");
        return Task.FromResult(enabled || !TerminateFails ? Result.Success() : Result.Failure(SecurityGovernanceErrors.SourceNotFoundError(kind, id)));
    }

    public Task<Result> TerminateAsync(string kind, string id, CancellationToken ct)
    {
        Actions.Add($"terminate:{kind}:{id}");
        return Task.FromResult(TerminateFails ? Result.Failure(SecurityGovernanceErrors.SourceNotFoundError(kind, id)) : Result.Success());
    }

    public Task<Result<IReadOnlyList<SourceHealth>>> ListHealthAsync(CancellationToken ct) =>
        Task.FromResult(Result<IReadOnlyList<SourceHealth>>.Ok(new List<SourceHealth>()));
}

internal sealed class InMemoryRevocationEpoch : IRevocationEpoch
{
    public long Current { get; private set; } = 1;
    public void Bump() => Current++;
}

internal sealed class InMemoryAuditLogStore : IAuditLogStore
{
    private readonly List<AuditEntry> _entries = new();
    public Task<Result<AuditEntry>> AppendAsync(AuditEntry e, CancellationToken ct) { _entries.Add(e); return Task.FromResult(Result<AuditEntry>.Ok(e)); }
    public Task<AuditEntry?> GetLastAsync(CancellationToken ct) => Task.FromResult(_entries.Count == 0 ? null : _entries[^1]);
    public Task<IReadOnlyList<AuditEntry>> GetAllAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<AuditEntry>>(_entries);
}

internal sealed class InMemoryGrantStore : IGrantStore
{
    private readonly Dictionary<PolicyGrantId, PolicyGrant> _grants = new();
    public void Seed(PolicyGrant g) => _grants[g.GrantId] = g;

    public Task<Result<PolicyGrantId>> IssueAsync(PolicyGrant grant, IClock clock, CancellationToken ct)
    {
        _grants[grant.GrantId] = grant;
        return Task.FromResult(Result<PolicyGrantId>.Ok(grant.GrantId));
    }

    public Task<Result<PolicyGrant>> GetAsync(PolicyGrantId id, CancellationToken ct) =>
        Task.FromResult(_grants.TryGetValue(id, out var g) ? Result<PolicyGrant>.Ok(g) : Result<PolicyGrant>.Fail(SecurityGovernanceErrors.GrantNotFoundError(id.Value)));

    public Task<Result<IReadOnlyList<PolicyGrant>>> ListByAutomationAsync(string automationId, string revisionId, CancellationToken ct) =>
        Task.FromResult(Result<IReadOnlyList<PolicyGrant>>.Ok(_grants.Values.Where(g => g.AutomationId == automationId && g.RevisionId == revisionId).ToList()));

    public Task<Result<IReadOnlyList<PolicyGrant>>> ListActiveGrantsAsync(string capabilityStableId, string sourceKind, string sourceId, string schemaSha256, DateTimeOffset nowUtc, CancellationToken ct) =>
        Task.FromResult(Result<IReadOnlyList<PolicyGrant>>.Ok(_grants.Values.Where(g => g.RevokedAtUtc is null && g.ExpiresAtUtc > nowUtc).ToList()));

    public Task<Result<IReadOnlyList<PolicyGrant>>> ListActiveAsync(DateTimeOffset nowUtc, CancellationToken ct = default) =>
        Task.FromResult(Result<IReadOnlyList<PolicyGrant>>.Ok(_grants.Values.Where(g => g.RevokedAtUtc is null && g.ExpiresAtUtc > nowUtc).ToList()));

    public Task<Result<PolicyGrant>> RevokeAsync(PolicyGrantId id, IClock clock, CancellationToken ct)
    {
        if (!_grants.TryGetValue(id, out var g)) return Task.FromResult(Result<PolicyGrant>.Fail(SecurityGovernanceErrors.GrantNotFoundError(id.Value)));
        var revoked = g with { RevokedAtUtc = clock.UtcNow };
        _grants[id] = revoked;
        return Task.FromResult(Result<PolicyGrant>.Ok(revoked));
    }
}

internal sealed class InMemoryAutomationRepository : IAutomationRepository
{
    public List<AutomationDefinition> Defs { get; } = new();

    public Task<Result<AutomationDefinition>> GetAsync(AutomationId id, CancellationToken ct) =>
        Task.FromResult(Defs.FirstOrDefault(d => d.Id == id) is { } d ? Result<AutomationDefinition>.Ok(d) : Result<AutomationDefinition>.Fail(SecurityGovernanceErrors.SourceNotFoundError("auto", id.Value)));

    public Task<Result<IReadOnlyList<AutomationDefinition>>> ListBySpaceAsync(SpaceId spaceId, bool includeDeleted, CancellationToken ct) =>
        Task.FromResult(Result<IReadOnlyList<AutomationDefinition>>.Ok(Defs.Where(d => d.SpaceId == spaceId && (includeDeleted || d.Lifecycle != AutomationLifecycle.Deleted)).ToList()));

    public Task<Result<IReadOnlyList<AutomationRevision>>> GetRevisionsAsync(AutomationId id, CancellationToken ct) =>
        Task.FromResult(Result<IReadOnlyList<AutomationRevision>>.Ok(new List<AutomationRevision>()));

    public Task<Result<AutomationRevision>> GetRevisionAsync(AutomationRevisionId revisionId, CancellationToken ct) =>
        Task.FromResult(Result<AutomationRevision>.Fail(SecurityGovernanceErrors.SourceNotFoundError("rev", revisionId.Value)));

    public Task<Result<AutomationDefinition>> SaveAsync(AutomationDefinition definition, AutomationRevision? newRevision, CancellationToken ct)
    {
        var i = Defs.FindIndex(d => d.Id == definition.Id);
        if (i >= 0) Defs[i] = definition; else Defs.Add(definition);
        return Task.FromResult(Result<AutomationDefinition>.Ok(definition));
    }

    public Task<Result<IReadOnlyList<AutomationDefinition>>> ListEnabledAsync(CancellationToken ct) =>
        Task.FromResult(Result<IReadOnlyList<AutomationDefinition>>.Ok(Defs.Where(d => d.Lifecycle == AutomationLifecycle.Enabled).ToList()));
}
