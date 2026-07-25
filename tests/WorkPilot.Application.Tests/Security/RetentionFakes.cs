using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Application.Automation.Run;
using WorkPilot.Application.Permission.Policy;
using WorkPilot.Application.Security;
using WorkPilot.Application.Security.Governance;
using WorkPilot.Application.Security.Retention;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation.Run;
using WorkPilot.Domain.PermissionGovernance;
using WorkPilot.Domain.Security;
using WorkPilot.Domain.Security.Audit;
using WorkPilot.Domain.Security.Retention;

namespace WorkPilot.Application.Tests.Security;

internal sealed class FakeRetentionSettingsStore : IRetentionSettingsStore
{
    private RetentionSettings _current;
    public RetentionSettings? LastSaved { get; private set; }
    public FakeRetentionSettingsStore(RetentionSettings? initial = null) => _current = initial ?? RetentionSettings.Default;
    public Task<Result<RetentionSettings>> GetAsync(CancellationToken ct = default) =>
        Task.FromResult(Result<RetentionSettings>.Ok(_current));
    public Task<Result> SaveAsync(RetentionSettings settings, CancellationToken ct = default)
    {
        _current = settings;
        LastSaved = settings;
        return Task.FromResult(Result.Success());
    }
}

internal sealed class FakeRetentionStore : IRetentionStore
{
    private readonly Dictionary<RunId, (RunStatus Status, DateTimeOffset FinishedAt)> _runs = new();
    private readonly Dictionary<IncidentId, (IncidentState State, DateTimeOffset LastSeen)> _incidents = new();
    private readonly HashSet<RunId> _deletedRuns = new();
    private int _eventCalls;
    private int _auditCalls;

    public int RunCascadeDeletes;
    public int EventDeletes;
    public int AuditDeletes;
    public int IncidentDeletes;

    public void SeedRun(RunId id, RunStatus status, DateTimeOffset finishedAt) => _runs[id] = (status, finishedAt);
    public void SeedIncident(IncidentId id, IncidentState state, DateTimeOffset lastSeen) => _incidents[id] = (state, lastSeen);
    public bool WasRunDeleted(RunId id) => _deletedRuns.Contains(id);
    public bool WasIncidentDeleted(IncidentId id) => !_incidents.ContainsKey(id);

    public Task<Result<IReadOnlyList<RunId>>> GetDeletableRunIdsAsync(DateTimeOffset runCutoff, int batchSize, CancellationToken ct = default)
    {
        var eligible = _runs
            .Where(r => IsTerminal(r.Value.Status) && r.Value.FinishedAt < runCutoff && !_deletedRuns.Contains(r.Key))
            .Select(r => r.Key).Take(batchSize).ToArray();
        return Task.FromResult(Result<IReadOnlyList<RunId>>.Ok(eligible));
    }

    public Task<Result<int>> DeleteRunCascadeAsync(RunId id, CancellationToken ct = default)
    {
        if (_runs.Remove(id)) { _deletedRuns.Add(id); RunCascadeDeletes++; return Task.FromResult(Result<int>.Ok(1)); }
        return Task.FromResult(Result<int>.Ok(0));
    }

    public Task<Result<int>> DeleteRunEventsOlderThanAsync(DateTimeOffset eventCutoff, int batchSize, CancellationToken ct = default)
    {
        var n = _eventCalls++ == 0 ? 7 : 0;
        EventDeletes += n;
        return Task.FromResult(Result<int>.Ok(n));
    }

    public Task<Result<int>> DeleteAuditRecordsOlderThanAsync(DateTimeOffset auditCutoff, int batchSize, CancellationToken ct = default)
    {
        var n = _auditCalls++ == 0 ? 5 : 0;
        AuditDeletes += n;
        return Task.FromResult(Result<int>.Ok(n));
    }

    public Task<Result<int>> DeleteResolvedIncidentsOlderThanAsync(DateTimeOffset auditCutoff, int batchSize, CancellationToken ct = default)
    {
        var toDelete = _incidents.Where(i => i.Value.State == IncidentState.Resolved && i.Value.LastSeen < auditCutoff).ToList();
        foreach (var kv in toDelete) _incidents.Remove(kv.Key);
        IncidentDeletes += toDelete.Count;
        return Task.FromResult(Result<int>.Ok(toDelete.Count));
    }

    private static bool IsTerminal(RunStatus s) =>
        s is RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled;
}

internal sealed class FakeAuditLogStore : IAuditLogStore
{
    private readonly List<AuditEntry> _entries = new();
    public IReadOnlyList<AuditEntry> Entries => _entries;
    public Task<Result<AuditEntry>> AppendAsync(AuditEntry entry, CancellationToken ct) { _entries.Add(entry); return Task.FromResult(Result<AuditEntry>.Ok(entry)); }
    public Task<AuditEntry?> GetLastAsync(CancellationToken ct) => Task.FromResult(_entries.Count == 0 ? null : _entries[^1]);
    public Task<IReadOnlyList<AuditEntry>> GetAllAsync(CancellationToken ct) => Task.FromResult((IReadOnlyList<AuditEntry>)_entries.ToArray());
}

internal sealed class FakeIncidentStore : IIncidentStore
{
    private readonly Dictionary<IncidentId, Incident> _incidents = new();
    public IReadOnlyList<Incident> All => _incidents.Values.ToArray();
    public int Inserts;
    public int Updates;
    public Task<Incident?> GetOpenByFingerprintAsync(string fingerprint, DateTimeOffset since, CancellationToken ct) =>
        Task.FromResult(_incidents.Values.FirstOrDefault(i => i.Fingerprint == fingerprint && i.State != IncidentState.Resolved));
    public Task<Incident?> GetByIdAsync(IncidentId id, CancellationToken ct) =>
        Task.FromResult(_incidents.TryGetValue(id, out var i) ? i : null);
    public Task InsertAsync(Incident incident, CancellationToken ct) { _incidents[incident.Id] = incident; Inserts++; return Task.CompletedTask; }
    public Task UpdateAsync(Incident incident, CancellationToken ct) { _incidents[incident.Id] = incident; Updates++; return Task.CompletedTask; }
    public Task<IReadOnlyList<Incident>> ListAsync(IncidentState? state, int limit, CancellationToken ct) =>
        Task.FromResult((IReadOnlyList<Incident>)_incidents.Values.Where(i => state is null || i.State == state).Take(limit).ToArray());
}

internal sealed class FakeSourceGovernanceBackend : ISourceGovernanceBackend
{
    public int SetEnabledCalls;
    public int TerminateCalls;

    public Task<Result> SetSourceEnabledAsync(string sourceKind, string sourceId, bool enabled, CancellationToken ct = default)
    {
        SetEnabledCalls++;
        return Task.FromResult(Result.Success());
    }

    public Task<Result> TerminateAsync(string sourceKind, string sourceId, CancellationToken ct = default)
    {
        TerminateCalls++;
        return Task.FromResult(Result.Success());
    }

    public Task<Result<IReadOnlyList<SourceHealth>>> ListHealthAsync(CancellationToken ct = default) =>
        Task.FromResult(Result<IReadOnlyList<SourceHealth>>.Ok(new List<SourceHealth>
        {
            new("connector", "c1", SourceHealthStatus.Healthy, null, DateTimeOffset.UtcNow),
            new("mcp", "m1", SourceHealthStatus.Degraded, "slow", DateTimeOffset.UtcNow)
        }));
}

internal sealed class FakeGrantStore : IGrantStore
{
    private readonly Dictionary<PolicyGrantId, PolicyGrant> _grants = new();

    public Task<Result<PolicyGrantId>> IssueAsync(PolicyGrant grant, IClock clock, CancellationToken ct = default)
    {
        _grants[grant.GrantId] = grant;
        return Task.FromResult(Result<PolicyGrantId>.Ok(grant.GrantId));
    }

    public Task<Result<PolicyGrant>> GetAsync(PolicyGrantId id, CancellationToken ct = default) =>
        _grants.TryGetValue(id, out var g)
            ? Task.FromResult(Result<PolicyGrant>.Ok(g))
            : Task.FromResult(Result<PolicyGrant>.Fail(RetentionAndExportErrors.PackageInvalidError("grant_not_found")));

    public Task<Result<IReadOnlyList<PolicyGrant>>> ListByAutomationAsync(string automationId, string revisionId, CancellationToken ct = default) =>
        Task.FromResult(Result<IReadOnlyList<PolicyGrant>>.Ok(Array.Empty<PolicyGrant>()));

    public Task<Result<IReadOnlyList<PolicyGrant>>> ListActiveGrantsAsync(
        string capabilityStableId, string sourceKind, string sourceId, string schemaSha256,
        DateTimeOffset nowUtc, CancellationToken ct = default) =>
        Task.FromResult(Result<IReadOnlyList<PolicyGrant>>.Ok(Array.Empty<PolicyGrant>()));

    public Task<Result<IReadOnlyList<PolicyGrant>>> ListActiveAsync(DateTimeOffset nowUtc, CancellationToken ct = default) =>
        Task.FromResult(Result<IReadOnlyList<PolicyGrant>>.Ok(Array.Empty<PolicyGrant>()));

    public Task<Result<PolicyGrant>> RevokeAsync(PolicyGrantId id, IClock clock, CancellationToken ct = default) =>
        _grants.TryGetValue(id, out var g)
            ? Task.FromResult(Result<PolicyGrant>.Ok(g))
            : Task.FromResult(Result<PolicyGrant>.Fail(RetentionAndExportErrors.PackageInvalidError("grant_not_found")));
}

internal sealed class FakeRunRepository : IRunRepository
{
    private readonly Dictionary<RunId, RunWithDetails?> _map = new();
    public void Seed(RunId id, RunWithDetails details) => _map[id] = details;
    public Task<Result<RunWithDetails?>> GetRunAsync(RunId id, CancellationToken ct) =>
        Task.FromResult(Result<RunWithDetails?>.Ok(_map.TryGetValue(id, out var d) ? d : null));
    public Task<Result> CreateRunAsync(AutomationRun run, RunSnapshot snapshot, TriggerOccurrence? occurrence, CancellationToken ct) => throw new InvalidOperationException();
    public Task<Result<RunListPage>> ListRunsAsync(RunQuery query, CancellationToken ct) => throw new InvalidOperationException();
    public Task<Result<bool>> TryClaimAsync(RunId id, string owner, DateTimeOffset leaseExpiresAt, CancellationToken ct) => throw new InvalidOperationException();
    public Task<Result> RequestCancellationAsync(RunId id, DateTimeOffset now, CancellationToken ct) => throw new InvalidOperationException();
    public Task<Result> CancelAsync(RunId id, DateTimeOffset now, CancellationToken ct) => throw new InvalidOperationException();
    public Task<Result> DeleteRunAsync(RunId id, CancellationToken ct) => throw new InvalidOperationException();
    public Task<Result> UpsertStepAsync(StepRun step, CancellationToken ct) => throw new InvalidOperationException();
    public Task<Result> PersistExecutionResultAsync(AutomationRun run, IReadOnlyList<StepRun> steps, IReadOnlyList<RunEvent> events, CancellationToken ct) => throw new InvalidOperationException();
    public Task<Result> AppendEventAsync(RunEvent ev, CancellationToken ct) => throw new InvalidOperationException();
    public Task<Result> AppendEventsAsync(IReadOnlyList<RunEvent> events, CancellationToken ct) => throw new InvalidOperationException();
}

internal sealed class FakeAuditIntegrityMonitor : IAuditIntegrityMonitor
{
    public bool ExternalCapabilityBlocked => false;
    public Task<AuditIntegrityReport> VerifyAsync(CancellationToken ct) =>
        Task.FromResult(new AuditIntegrityReport(true, 42, null, null));
}

internal sealed class FakeDiagnosticLogDirectory : IDiagnosticLogDirectory
{
    public string Directory { get; init; } = Path.GetTempPath();
    public string BaseName { get; init; } = "app";
}

internal sealed class FakeAppInfo : IAppInfo
{
    public string AppVersion => "1.5.0-test";
    public string OsVersion => "Windows 10";
    public string Architecture => "x64";
    public int DatabaseSchemaVersion => 22;
}

internal sealed class FakeDiskSpaceProbe : IDiskSpaceProbe
{
    public long FreeBytes { get; set; } = 1024L * 1024 * 1024 * 1024;
    public long GetFreeBytes(string path) => FreeBytes;
}

internal sealed class FakeSecurityStateStore : ISecurityStateStore
{
    private readonly Dictionary<string, string> _kv = new();
    public Dictionary<string, string> Written => _kv;
    public Task<Result> SetAsync(string key, string value, CancellationToken ct = default)
    {
        _kv[key] = value;
        return Task.FromResult(Result.Success());
    }
    public Task<Result<string?>> GetAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(Result<string?>.Ok(_kv.TryGetValue(key, out var v) ? v : null));
}
