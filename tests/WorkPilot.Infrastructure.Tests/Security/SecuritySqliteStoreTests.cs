using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using WorkPilot.Application.Security;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Security;
using WorkPilot.Domain.Security.Audit;
using WorkPilot.Domain.Security.Detectors;
using WorkPilot.Infrastructure.Data;
using WorkPilot.Infrastructure.Security;
using Xunit;

namespace WorkPilot.Infrastructure.Tests.Security;

/// <summary>Round-trip tests for <see cref="SecuritySqliteStore"/> against the Migration 020 tables.</summary>
public sealed class SecuritySqliteStoreTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly IIdGenerator Ids = new SequentialIdGenerator();
    private static readonly byte[] AuditKey = new StaticAuditKeyProvider().GetKey();

    private static async Task<(SqliteConnection Connection, SecuritySqliteStore Store)> OpenStoreAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var migrator = new V15DatabaseMigrator(new FakeClock(FixedNow));
        await migrator.CreateSecurityTablesAsync(connection, CancellationToken.None);
        return (connection, new SecuritySqliteStore(connection));
    }

    private static string Hash64(string input)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }

    private static SecurityEvent SampleEvent()
    {
        var fingerprint = SecurityEventFingerprint.Compute(
            SecurityEventType.AuthFailureContinuous,
            new SourceReference("automation", "a1"),
            null,
            AutomationId.Parse("a1"),
            null);
        return new SecurityEvent(
            SecurityEventId.Create(Ids),
            FixedNow,
            SecurityEventType.AuthFailureContinuous,
            SecuritySeverity.High,
            fingerprint,
            new SourceReference("automation", "a1"),
            AutomationId.Parse("a1"),
            RunId.Parse("r1"),
            new Dictionary<string, string> { ["attempts"] = "7", ["window_min"] = "5" },
            DetectorConstants.DetectorVersion);
    }

    // ---- SEC-102: security events ----

    [Fact]
    public async Task SecurityEvent_round_trips_without_display_names()
    {
        var (connection, store) = await OpenStoreAsync();
        try
        {
            var original = SampleEvent();
            var append = await store.AppendAsync(original, CancellationToken.None);
            Assert.True(append.IsSuccess);

            var list = await store.ListRecentAsync(10, CancellationToken.None);
            Assert.Single(list);
            var round = list[0];

            Assert.Equal(original.Id, round.Id);
            Assert.Equal(SecurityEventType.AuthFailureContinuous, round.Type);
            Assert.Equal(SecuritySeverity.High, round.Severity);
            Assert.Equal(original.Fingerprint, round.Fingerprint);
            Assert.Equal(original.Source, round.Source);
            Assert.Equal(original.AutomationId, round.AutomationId);
            Assert.Equal(original.RunId, round.RunId);
            Assert.Equal(DetectorConstants.DetectorVersion, round.DetectorVersion);
            Assert.Equal(2, round.SafeEvidence.Count);
            Assert.Equal("7", round.SafeEvidence["attempts"]);
            Assert.Equal("5", round.SafeEvidence["window_min"]);
        }
        finally
        {
            connection.Dispose();
        }
    }

    [Fact]
    public async Task SecurityEvent_with_null_references_round_trips()
    {
        var (connection, store) = await OpenStoreAsync();
        try
        {
            var fingerprint = Hash64("no-source");
            var e = new SecurityEvent(
                SecurityEventId.Create(Ids),
                FixedNow,
                SecurityEventType.DiskSpaceLow,
                SecuritySeverity.Medium,
                fingerprint,
                null,
                null,
                null,
                new Dictionary<string, string> { ["reason"] = "disk" },
                DetectorConstants.DetectorVersion);

            Assert.True((await store.AppendAsync(e, CancellationToken.None)).IsSuccess);
            var round = (await store.ListRecentAsync(5, CancellationToken.None))[0];
            Assert.Null(round.Source);
            Assert.Null(round.AutomationId);
            Assert.Null(round.RunId);
        }
        finally
        {
            connection.Dispose();
        }
    }

    [Fact]
    public async Task ExistsRecent_async_detects_fingerprint_since()
    {
        var (connection, store) = await OpenStoreAsync();
        try
        {
            var e = SampleEvent();
            await store.AppendAsync(e, CancellationToken.None);
            Assert.True(await store.ExistsRecentAsync(e.Fingerprint, FixedNow.AddMinutes(-5), CancellationToken.None));
            Assert.False(await store.ExistsRecentAsync(e.Fingerprint, FixedNow.AddMinutes(5), CancellationToken.None));
        }
        finally
        {
            connection.Dispose();
        }
    }

    // ---- SEC-103: incidents ----

    [Fact]
    public async Task Incident_insert_get_open_update_cycle()
    {
        var (connection, store) = await OpenStoreAsync();
        try
        {
            var fingerprint = Hash64("incident-1");
            var incident = new Incident(
                IncidentId.Create(Ids),
                fingerprint,
                IncidentState.Open,
                SecuritySeverity.High,
                SecurityEventType.AuthFailureContinuous,
                FixedNow.AddMinutes(-3),
                FixedNow,
                1,
                new List<string> { Hash64("ev1") },
                null,
                null,
                null,
                FixedNow.AddMinutes(-3),
                FixedNow,
                null);

            await store.InsertAsync(incident, CancellationToken.None);

            var fetched = await store.GetOpenByFingerprintAsync(fingerprint, FixedNow.AddMinutes(-10), CancellationToken.None);
            Assert.NotNull(fetched);
            Assert.Equal(IncidentState.Open, fetched!.State);
            Assert.Equal(1, fetched.Count);
            Assert.Single(fetched.RecentEvidenceDigests);

            var updated = fetched with { State = IncidentState.Acknowledged, Severity = SecuritySeverity.Critical, Count = 4 };
            await store.UpdateAsync(updated, CancellationToken.None);

            var reFetched = await store.GetOpenByFingerprintAsync(fingerprint, FixedNow.AddMinutes(-10), CancellationToken.None);
            Assert.NotNull(reFetched);
            Assert.Equal(IncidentState.Acknowledged, reFetched!.State);
            Assert.Equal(SecuritySeverity.Critical, reFetched.Severity);
            Assert.Equal(4, reFetched.Count);
        }
        finally
        {
            connection.Dispose();
        }
    }

    [Fact]
    public async Task Incident_resolved_is_returned_for_reopen_window()
    {
        var (connection, store) = await OpenStoreAsync();
        try
        {
            var fingerprint = Hash64("incident-resolved");
            var incident = new Incident(
                IncidentId.Create(Ids),
                fingerprint,
                IncidentState.Resolved,
                SecuritySeverity.Low,
                SecurityEventType.ApprovalRejectionBurst,
                FixedNow.AddHours(-2),
                FixedNow.AddHours(-1),
                3,
                new List<string> { Hash64("e") },
                IncidentResolutionCode.Remediated.ToString(),
                "fixed",
                FixedNow,
                FixedNow.AddHours(-2),
                FixedNow.AddHours(-1),
                null);

            await store.InsertAsync(incident, CancellationToken.None);
            // The aggregator relies on GetOpenByFingerprintAsync returning the resolved incident so it
            // can re-open it (doc 06 §3). It must not be filtered out by an IsClosed check.
            var fetched = await store.GetOpenByFingerprintAsync(fingerprint, FixedNow.AddHours(-3), CancellationToken.None);
            Assert.NotNull(fetched);
            Assert.Equal(IncidentState.Resolved, fetched!.State);
            Assert.Equal("fixed", fetched.ResolutionNote);
        }
        finally
        {
            connection.Dispose();
        }
    }

    [Fact]
    public async Task Incident_list_filters_by_state()
    {
        var (connection, store) = await OpenStoreAsync();
        try
        {
            await store.InsertAsync(MakeIncident(Hash64("o1"), IncidentState.Open), CancellationToken.None);
            await store.InsertAsync(MakeIncident(Hash64("r1"), IncidentState.Resolved), CancellationToken.None);

            var open = await store.ListAsync(IncidentState.Open, 50, CancellationToken.None);
            Assert.Single(open);
            Assert.Equal(IncidentState.Open, open[0].State);

            var all = await store.ListAsync(null, 50, CancellationToken.None);
            Assert.Equal(2, all.Count);
        }
        finally
        {
            connection.Dispose();
        }
    }

    private static Incident MakeIncident(string fingerprint, IncidentState state) =>
        new(
            IncidentId.Create(Ids),
            fingerprint,
            state,
            SecuritySeverity.Medium,
            SecurityEventType.PolicyDenialBurst,
            FixedNow.AddMinutes(-5),
            FixedNow,
            1,
            new List<string> { Hash64("x") },
            null,
            null,
            null,
            FixedNow.AddMinutes(-5),
            FixedNow,
            null);

    // ---- SEC-106: audit log ----

    [Fact]
    public async Task Audit_log_preserves_hmac_and_order()
    {
        var (connection, store) = await OpenStoreAsync();
        try
        {
            var first = BuildEntry(null);
            var second = BuildEntry(first);

            Assert.True((await store.AppendAsync(first, CancellationToken.None)).IsSuccess);
            Assert.True((await store.AppendAsync(second, CancellationToken.None)).IsSuccess);

            var all = await store.GetAllAsync(CancellationToken.None);
            Assert.Equal(2, all.Count);
            Assert.Equal(1L, all[0].Sequence);
            Assert.Equal(2L, all[1].Sequence);
            Assert.Equal(first.Hmac, all[0].Hmac);
            Assert.Equal(second.Hmac, all[1].Hmac);

            var last = await store.GetLastAsync(CancellationToken.None);
            Assert.NotNull(last);
            Assert.Equal(2L, last!.Sequence);
            Assert.Equal(second.Hmac, last.Hmac);
        }
        finally
        {
            connection.Dispose();
        }
    }

    private static AuditEntry BuildEntry(AuditEntry? previous)
    {
        var content = new AuditEntry(
            0,
            FixedNow,
            AuditCategory.Governance,
            "policy.evaluated",
            "system",
            "{\"subject\":\"policy:builtin\"}",
            "{\"decision\":\"allow\"}",
            "{\"detail\":\"none\"}",
            "0",
            "0",
            FixedNow);
        return AuditChain.Link(AuditKey, previous, content);
    }

    // ---- doc 06 §4: detector action idempotency ----

    [Fact]
    public async Task Detector_action_mark_applied_is_idempotent()
    {
        var (connection, store) = await OpenStoreAsync();
        try
        {
            Assert.True(await store.TryMarkAppliedAsync("DET-001:automation:a1", CancellationToken.None));
            Assert.False(await store.TryMarkAppliedAsync("DET-001:automation:a1", CancellationToken.None));
            Assert.True(await store.TryMarkAppliedAsync("DET-002:automation:a1", CancellationToken.None));
            Assert.False(await store.TryMarkAppliedAsync("DET-002:automation:a1", CancellationToken.None));
        }
        finally
        {
            connection.Dispose();
        }
    }

    // ---- cross-table integrity ----

    [Fact]
    public async Task Cross_table_write_then_read_all_tables()
    {
        var (connection, store) = await OpenStoreAsync();
        try
        {
            var e = SampleEvent();
            Assert.True((await store.AppendAsync(e, CancellationToken.None)).IsSuccess);

            var incident = MakeIncident(e.Fingerprint, IncidentState.Open);
            await store.InsertAsync(incident, CancellationToken.None);

            Assert.True((await store.AppendAsync(BuildEntry(null), CancellationToken.None)).IsSuccess);
            Assert.True(await store.TryMarkAppliedAsync("DET-001:automation:a1", CancellationToken.None));

            Assert.Single(await store.ListRecentAsync(10, CancellationToken.None));
            var incidents = await store.ListAsync(null, 10, CancellationToken.None);
            Assert.Single(incidents);
            Assert.Single(await store.GetAllAsync(CancellationToken.None));
            Assert.False(await store.TryMarkAppliedAsync("DET-001:automation:a1", CancellationToken.None));
        }
        finally
        {
            connection.Dispose();
        }
    }
}

internal sealed class SequentialIdGenerator : IIdGenerator
{
    private long _counter;
    public string NewId() => $"id-{Interlocked.Increment(ref _counter):D10}";
}
