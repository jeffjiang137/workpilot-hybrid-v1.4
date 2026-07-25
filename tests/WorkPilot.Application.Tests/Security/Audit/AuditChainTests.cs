using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Application.Security;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Security;
using WorkPilot.Domain.Security.Audit;
using Xunit;

namespace WorkPilot.Application.Tests.Security.Audit;

public sealed class AuditChainTests
{
    private static readonly byte[] Key = new StaticAuditKeyProvider().GetKey();
    private static readonly IClock Clock = new FakeClock(new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero));

    private static AuditEntry Make(long seq, string action, string prev, string hmac) =>
        new(seq, Clock.UtcNow, AuditCategory.Governance, action, "user", "{}", "", "{}", prev, hmac, Clock.UtcNow);

    [Fact]
    public void Linked_chain_verifies_intact()
    {
        AuditEntry? prev = null;
        var entries = new List<AuditEntry>();
        for (var i = 0; i < 5; i++)
        {
            prev = AuditChain.Link(Key, prev, Make(0, $"act_{i}", string.Empty, string.Empty));
            entries.Add(prev);
        }

        var report = AuditIntegrity.Verify(entries, Key);
        Assert.True(report.Intact);
        Assert.Equal(5, report.VerifiedCount);
    }

    [Fact]
    public void Tampered_payload_is_detected()
    {
        AuditEntry? prev = null;
        var entries = new List<AuditEntry>();
        for (var i = 0; i < 5; i++)
        {
            prev = AuditChain.Link(Key, prev, Make(0, $"act_{i}", string.Empty, string.Empty));
            entries.Add(prev);
        }

        // Mutate row 3's action (simulating DB edit).
        var mutated = entries[2] with { Action = "act_3_HACKED" };
        entries[2] = mutated;

        var report = AuditIntegrity.Verify(entries, Key);
        Assert.False(report.Intact);
        Assert.Equal(3, report.FirstBroken!.Sequence);
    }

    [Fact]
    public void Tampered_sequence_number_breaks_continuity()
    {
        AuditEntry? prev = null;
        var entries = new List<AuditEntry>();
        for (var i = 0; i < 4; i++)
        {
            prev = AuditChain.Link(Key, prev, Make(0, $"act_{i}", string.Empty, string.Empty));
            entries.Add(prev);
        }

        // Corrupt a sequence number (simulating row reorder / omission at the storage layer).
        entries[1] = entries[1] with { Sequence = 99 };

        var report = AuditIntegrity.Verify(entries, Key);
        Assert.False(report.Intact);
        Assert.NotNull(report.FirstBroken);
    }

    [Fact]
    public void Missing_middle_row_detected_as_gap()
    {
        AuditEntry? prev = null;
        var entries = new List<AuditEntry>();
        for (var i = 0; i < 4; i++)
        {
            prev = AuditChain.Link(Key, prev, Make(0, $"act_{i}", string.Empty, string.Empty));
            entries.Add(prev);
        }

        entries.RemoveAt(1); // drop sequence 2

        var report = AuditIntegrity.Verify(entries, Key);
        Assert.False(report.Intact);
    }

    [Fact]
    public async Task Writer_appends_verifiable_chain()
    {
        var store = new InMemoryAuditLogStore();
        var writer = new AuditLogWriter(store, new StaticAuditKeyProvider(), Clock);

        for (var i = 0; i < 6; i++)
            await writer.AppendAsync(AuditCategory.Governance, $"gov_{i}", "user", "{\"id\":\"a\"}", "", "{\"ok\":1}", CancellationToken.None);

        var all = await store.GetAllAsync(CancellationToken.None);
        Assert.Equal(6, all.Count);
        Assert.Equal(1, all[0].Sequence);
        Assert.Equal("0", all[0].PrevHmac);

        var report = AuditIntegrity.Verify(all, Key);
        Assert.True(report.Intact);
    }

    [Fact]
    public async Task IntegrityService_blocks_external_capability_on_break_and_emits_det008()
    {
        var store = new InMemoryAuditLogStore();
        var writer = new AuditLogWriter(store, new StaticAuditKeyProvider(), Clock);
        await writer.AppendAsync(AuditCategory.Governance, "gov_1", "user", "{}", "", "{}", CancellationToken.None);

        var emitter = new RecordingEmitter();
        var service = new AuditIntegrityService(store, new StaticAuditKeyProvider(), Clock, emitter);

        var ok = await service.VerifyAsync(CancellationToken.None);
        Assert.True(ok.Intact);
        Assert.False(service.ExternalCapabilityBlocked);

        // Tamper the stored entry directly in the store.
        var last = await store.GetLastAsync(CancellationToken.None);
        store.Tamper(0, last! with { Action = "gov_1_HACKED" });

        var broken = await service.VerifyAsync(CancellationToken.None);
        Assert.False(broken.Intact);
        Assert.True(service.ExternalCapabilityBlocked);
        Assert.Single(emitter.Events);
        Assert.Equal(SecurityEventType.AuditIntegrityFailure, emitter.Events[0].Type);
        Assert.Equal(SecuritySeverity.Critical, emitter.Events[0].Severity);
    }

    [Fact]
    public async Task Exporter_writes_one_json_line_per_entry()
    {
        var store = new InMemoryAuditLogStore();
        var writer = new AuditLogWriter(store, new StaticAuditKeyProvider(), Clock);
        await writer.AppendAsync(AuditCategory.Incident, "incident_resolved", "user", "{\"id\":\"x\"}", "", "{}", CancellationToken.None);
        await writer.AppendAsync(AuditCategory.Detector, "detector_action", "detector:DET-008", "{}", "", "{}", CancellationToken.None);

        var exporter = new AuditExporter(store);
        using var ms = new MemoryStream();
        var n = await exporter.ExportJsonLAsync(ms, CancellationToken.None);

        Assert.Equal(2, n);
        ms.Position = 0;
        var text = new StreamReader(ms).ReadToEnd();
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Contains("incident_resolved", lines[0]);
        Assert.Contains("detector_action", lines[1]);
        Assert.StartsWith("{", lines[0]);
    }
}

internal sealed class InMemoryAuditLogStore : IAuditLogStore
{
    private readonly List<AuditEntry> _entries = new();
    private long _seq;

    public Task<Result<AuditEntry>> AppendAsync(AuditEntry entry, CancellationToken ct)
    {
        var stored = entry with { Sequence = ++_seq };
        _entries.Add(stored);
        return Task.FromResult(Result<AuditEntry>.Ok(stored));
    }

    public Task<AuditEntry?> GetLastAsync(CancellationToken ct)
        => Task.FromResult<AuditEntry?>(_entries.OrderBy(e => e.Sequence).LastOrDefault());

    public Task<IReadOnlyList<AuditEntry>> GetAllAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<AuditEntry>>(_entries.OrderBy(e => e.Sequence).ToList());

    public void Tamper(int index, AuditEntry replacement) => _entries[index] = replacement;
}

internal sealed class RecordingEmitter : ISecurityEventEmitter
{
    public List<SecurityEvent> Events { get; } = new();
    public Task EmitAsync(SecurityEvent e, CancellationToken ct)
    {
        Events.Add(e);
        return Task.CompletedTask;
    }
}
