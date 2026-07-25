using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Application.Automation.Run;
using WorkPilot.Application.Security;
using WorkPilot.Application.Security.Retention;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation.Run;
using WorkPilot.Domain.Automation.Run.Redaction;
using WorkPilot.Domain.Security;
using WorkPilot.Domain.Security.Audit;
using WorkPilot.Domain.Security.Retention;
using Xunit;

namespace WorkPilot.Application.Tests.Security;

public class RetentionPolicyDomainTests
{
    [Fact]
    public void Policy_Clamp_KeepsInRangeAndClampsOutOfRange()
    {
        var p = new RetentionPolicy(1000, -5, 9999).Clamp();
        Assert.Equal(Limits.V1_5.RetentionMaxRunDays, p.RunDays);
        Assert.Equal(Limits.V1_5.RetentionMinEventDays, p.EventDays);
        Assert.Equal(Limits.V1_5.RetentionMaxAuditDays, p.AuditDays);

        var inRange = new RetentionPolicy(120, 30, 300).Clamp();
        Assert.Equal(120, inRange.RunDays);
        Assert.Equal(30, inRange.EventDays);
        Assert.Equal(300, inRange.AuditDays);
    }

    [Fact]
    public void Policy_ComputeCutoffs_BackDatesCorrectly()
    {
        var now = new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero);
        var p = new RetentionPolicy(90, 30, 180);
        var (runCut, eventCut, auditCut) = p.ComputeCutoffs(now);
        Assert.Equal(now.AddDays(-90), runCut);
        Assert.Equal(now.AddDays(-30), eventCut);
        Assert.Equal(now.AddDays(-180), auditCut);
    }

    [Fact]
    public void Settings_CleanupAlreadyRunToday_DetectsSameDay()
    {
        var now = new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);
        var s1 = RetentionSettings.Default with { LastCleanupAtUtc = now };
        Assert.True(s1.CleanupAlreadyRunToday(now));

        var s2 = RetentionSettings.Default with { LastCleanupAtUtc = now.AddDays(-1) };
        Assert.False(s2.CleanupAlreadyRunToday(now));

        Assert.False(RetentionSettings.Default.CleanupAlreadyRunToday(now)); // null never ran
    }
}

public class RetentionSettingsServiceTests
{
    [Fact]
    public async Task SaveAsync_ClampsOutOfRangePolicy()
    {
        var store = new FakeRetentionSettingsStore();
        var svc = new RetentionSettingsService(store);

        var outOfRange = new RetentionSettings(new RetentionPolicy(9999, -10, 5000), null);
        var res = await svc.SaveAsync(outOfRange);

        Assert.True(res.IsSuccess);
        Assert.Equal(Limits.V1_5.RetentionMaxRunDays, res.Value!.Policy.RunDays);
        Assert.Equal(Limits.V1_5.RetentionMinEventDays, res.Value.Policy.EventDays);
        Assert.Equal(Limits.V1_5.RetentionMaxAuditDays, res.Value.Policy.AuditDays);
        Assert.NotNull(store.LastSaved);
        Assert.Equal(Limits.V1_5.RetentionMaxRunDays, store.LastSaved!.Policy.RunDays);
    }

    [Fact]
    public async Task GetAsync_ReturnsStored()
    {
        var store = new FakeRetentionSettingsStore();
        var svc = new RetentionSettingsService(store);
        var res = await svc.GetAsync();
        Assert.True(res.IsSuccess);
        Assert.Equal(Limits.V1_5.RetentionDefaultRunDays, res.Value!.Policy.RunDays);
    }
}

public class DataRetentionCleanerTests
{
    private static DataRetentionCleaner MakeCleaner(
        FakeRetentionSettingsStore settings, FakeRetentionStore store, FakeAuditLogStore audit, DateTimeOffset now) =>
        new(settings, store, new AuditLogWriter(audit, new StaticAuditKeyProvider(), new FakeClock(now)), new FakeClock(now));

    [Fact]
    public async Task RunAsync_SkipsWhenAlreadyRunToday()
    {
        var now = new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);
        var settings = new FakeRetentionSettingsStore(RetentionSettings.Default with { LastCleanupAtUtc = now });
        var store = new FakeRetentionStore();
        var audit = new FakeAuditLogStore();

        var res = await MakeCleaner(settings, store, audit, now).RunAsync();

        Assert.True(res.IsSuccess);
        Assert.True(res.Value!.SkippedBecauseAlreadyRunToday);
        Assert.False(res.Value.Ran);
        Assert.Empty(audit.Entries); // skip writes no audit
    }

    [Fact]
    public async Task RunNowAsync_DeletesTerminalRunsAndResolvedIncidents_ProtectsOpen()
    {
        var now = new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);
        var settings = new FakeRetentionSettingsStore(); // LastCleanupAtUtc null -> runs
        var store = new FakeRetentionStore();
        var audit = new FakeAuditLogStore();

        var terminalRun = RunId.Parse("run_term");
        var openRun = RunId.Parse("run_open");
        store.SeedRun(terminalRun, RunStatus.Completed, now.AddDays(-200));
        store.SeedRun(openRun, RunStatus.Running, now.AddDays(-200)); // non-terminal must survive

        var resolvedInc = IncidentId.Parse("inc_res");
        var openInc = IncidentId.Parse("inc_open");
        store.SeedIncident(resolvedInc, IncidentState.Resolved, now.AddDays(-200));
        store.SeedIncident(openInc, IncidentState.Open, now.AddDays(-200)); // open must survive

        var res = await MakeCleaner(settings, store, audit, now).RunNowAsync();

        Assert.True(res.IsSuccess, res.IsSuccess ? "" : res.Error?.Code);
        Assert.True(res.Value!.Ran);
        Assert.True(store.WasRunDeleted(terminalRun));
        Assert.False(store.WasRunDeleted(openRun), "non-terminal run must never be deleted");
        Assert.True(store.WasIncidentDeleted(resolvedInc));
        Assert.False(store.WasIncidentDeleted(openInc), "open incident must never be auto-deleted");
        Assert.Single(audit.Entries); // exactly one cleanup audit, no business ids

        var saved = settings.LastSaved;
        Assert.NotNull(saved);
        Assert.NotNull(saved!.LastCleanupAtUtc);
    }
}

public class DiskSpaceGuardTests
{
    private static DiskSpaceGuard MakeGuard(
        FakeDiskSpaceProbe probe, FakeRetentionSettingsStore settings, FakeRetentionStore store,
        FakeIncidentStore incidents, FakeSecurityStateStore state, DateTimeOffset now)
    {
        var audit = new FakeAuditLogStore();
        var cleaner = new DataRetentionCleaner(settings, store, new AuditLogWriter(audit, new StaticAuditKeyProvider(), new FakeClock(now)), new FakeClock(now));
        return new DiskSpaceGuard(probe, cleaner, incidents, new SequentialIdGenerator(), new FakeClock(now), state);
    }

    [Fact]
    public async Task LowFree_StopsNewAutomationAndRaisesHighIncident_ThenDedupes()
    {
        var now = new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);
        var probe = new FakeDiskSpaceProbe { FreeBytes = 100L * 1024 * 1024 }; // 100 MiB < 200 MiB
        var incidents = new FakeIncidentStore();
        var state = new FakeSecurityStateStore();

        var guard = MakeGuard(probe, new FakeRetentionSettingsStore(), new FakeRetentionStore(), incidents, state, now);
        var res = await guard.CheckAsync("C:\\data");

        Assert.True(res.IsSuccess, res.IsSuccess ? "" : res.Error?.Code);
        var v = res.Value!;
        Assert.True(v.Low);
        Assert.True(v.StopNewAutomation);
        Assert.True(v.CleanupTriggered);
        Assert.True(v.IncidentRaised);

        Assert.True(state.Written.ContainsKey("automation_suspended_disk_low"));
        Assert.Equal("1", state.Written["automation_suspended_disk_low"]);

        Assert.Single(incidents.All);
        Assert.Equal(SecuritySeverity.High, incidents.All[0].Severity);

        // second call de-dupes the open incident
        var res2 = await guard.CheckAsync("C:\\data");
        Assert.True(res2.IsSuccess);
        Assert.True(res2.Value!.StopNewAutomation);
        Assert.False(res2.Value.IncidentRaised, "open incident should be de-duplicated");
        Assert.Equal(2, incidents.All[0].Count);
    }

    [Fact]
    public async Task HealthyFree_NoAction()
    {
        var now = new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);
        var probe = new FakeDiskSpaceProbe { FreeBytes = 1024L * 1024 * 1024 }; // 1 GiB
        var incidents = new FakeIncidentStore();
        var state = new FakeSecurityStateStore();

        var guard = MakeGuard(probe, new FakeRetentionSettingsStore(), new FakeRetentionStore(), incidents, state, now);
        var res = await guard.CheckAsync("C:\\data");

        Assert.True(res.IsSuccess);
        var v = res.Value!;
        Assert.False(v.Low);
        Assert.False(v.StopNewAutomation);
        Assert.False(v.CleanupTriggered);
        Assert.False(v.IncidentRaised);
        Assert.Empty(incidents.All);
        Assert.False(state.Written.ContainsKey("automation_suspended_disk_low"));
    }
}

public class RunReportExporterTests
{
    private static RunWithDetails BuildDetails(DateTimeOffset now, RunStatus status, string sentinel)
    {
        var run = RunFakes.CapabilityRun(now) with { Status = status };
        var snapshot = RunSnapshot.Create(
            RunSnapshotId.Parse("snap_1"),
            AutomationRevisionId.Parse("rev_1"),
            ExpertRevisionId.Parse("exp_1"),
            policySnapshotJson: "{\"sentinel\":\"" + sentinel + "\"}",
            capabilitySnapshotJson: "{\"cap\":\"x\"}",
            workflowSnapshotJson: "{\"wf\":\"y\"}",
            bindingSnapshotJson: "{\"b\":\"z\"}",
            budgetSnapshotJson: "{\"bud\":\"1\"}",
            revocationEpoch: 0,
            algorithmVersionsJson: "{\"v\":\"1\"}",
            canonicalSha256: new string('a', 64),
            createdAtUtc: now);

        var steps = new List<StepRun>
        {
            RunFakes.DummyStep("n1", "run_1") with
            {
                Status = StepRunStatus.Succeeded,
                StartedAtUtc = now,
                FinishedAtUtc = now.AddMinutes(1),
                DurationMs = 60_000
            }
        };

        var events = new List<RunEvent>
        {
            RunEvent.Create(
                RunEventId.Parse("evt_1"),
                RunId.Parse("run_1"),
                "node_started",
                RunEventLevel.Info,
                "RUN_NODE_STARTED",
                "Run.Node.Started",
                "{}",
                "corr_1",
                now)
        };

        return new RunWithDetails(run, snapshot, steps, events);
    }

    [Fact]
    public async Task BuildAsync_ExcludesSensitiveSnapshot_AndComputesHash()
    {
        var now = new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);
        var sentinel = "SENTINEL_PROMPT_SECRET_" + Guid.NewGuid();
        var repo = new FakeRunRepository();
        repo.Seed(RunId.Parse("run_1"), BuildDetails(now, RunStatus.Running, sentinel));

        var exporter = new RunReportExporter(repo, new FakeClock(now));
        var res = await exporter.BuildAsync(RunId.Parse("run_1"));

        Assert.True(res.IsSuccess, res.IsSuccess ? "" : res.Error?.Code);
        var report = res.Value!;
        Assert.Equal("run_1", report.Run.Id);
        Assert.False(string.IsNullOrEmpty(report.Hash));
        Assert.Equal(64, report.Hash.Length);
        Assert.Equal(Limits.V1_5.RunReportSchemaVersion, report.SchemaVersion);
        Assert.Single(report.Steps);
        Assert.Single(report.Events);

        // The report must NOT leak prompt/parameters/results (which live only in the snapshot).
        var json = JsonSerializer.Serialize(report);
        Assert.DoesNotContain(sentinel, json);
        Assert.DoesNotContain("snap_1", json);
    }

    [Fact]
    public async Task BuildAsync_WaitingApproval_SetsDecisionTraceSummary()
    {
        var now = new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);
        var repo = new FakeRunRepository();
        repo.Seed(RunId.Parse("run_1"), BuildDetails(now, RunStatus.WaitingApproval, "sentinel_x"));

        var exporter = new RunReportExporter(repo, new FakeClock(now));
        var res = await exporter.BuildAsync(RunId.Parse("run_1"));

        Assert.True(res.IsSuccess);
        Assert.Equal("approval-required", res.Value!.DecisionTraceSummary);
    }

    [Fact]
    public async Task BuildAsync_MissingRun_ReturnsNotFound()
    {
        var exporter = new RunReportExporter(new FakeRunRepository(), new FakeClock(DateTimeOffset.UtcNow));
        var res = await exporter.BuildAsync(RunId.Parse("missing"));
        Assert.False(res.IsSuccess);
        Assert.Equal("RET_RUN_REPORT_NOT_FOUND", res.Error!.Code);
    }
}

public class SupportBundleRequestValidationTests
{
    [Fact]
    public void Validate_NoOutputPath_Fails()
    {
        var r = new SupportBundleRequest("", new HashSet<SupportPackageCategory> { SupportPackageCategory.Configuration }, Array.Empty<RunId>());
        var v = r.Validate();
        Assert.False(v.IsSuccess);
        Assert.Equal("RET_PKG_INVALID", v.Error!.Code);
    }

    [Fact]
    public void Validate_NoCategory_Fails()
    {
        var r = new SupportBundleRequest("out.zip", new HashSet<SupportPackageCategory>(), Array.Empty<RunId>());
        var v = r.Validate();
        Assert.False(v.IsSuccess);
        Assert.Equal("RET_PKG_INVALID", v.Error!.Code);
    }

    [Fact]
    public void Validate_RunReportsWithoutIds_Fails()
    {
        var r = new SupportBundleRequest("out.zip", new HashSet<SupportPackageCategory> { SupportPackageCategory.RunReports }, Array.Empty<RunId>());
        var v = r.Validate();
        Assert.False(v.IsSuccess);
        Assert.Equal("RET_PKG_INVALID", v.Error!.Code);
    }

    [Fact]
    public void Validate_RunReportsNotSelected_DefaultsOk()
    {
        var r = new SupportBundleRequest("out.zip", new HashSet<SupportPackageCategory> { SupportPackageCategory.Configuration }, Array.Empty<RunId>());
        Assert.True(r.Validate().IsSuccess);
    }

    [Fact]
    public void Validate_TooManyRunIds_Fails()
    {
        var ids = Enumerable.Range(0, Limits.V1_5.SupportBundleMaxRunReports + 1)
            .Select(i => RunId.Parse("r" + i)).ToArray();
        var r = new SupportBundleRequest("out.zip", new HashSet<SupportPackageCategory> { SupportPackageCategory.RunReports }, ids);
        var v = r.Validate();
        Assert.False(v.IsSuccess);
        Assert.Equal("RET_PKG_RUN_LIMIT", v.Error!.Code);
    }
}

public class SupportBundleBuilderTests
{
    private static SupportBundleBuilder MakeBuilder(
        string diagDir, ISet<string> canaryTokens, DateTimeOffset now, IRunRepository? runRepo = null, IAuditLogStore? audit = null,
        ISecretMatcher? matcher = null) =>
        new(
            new FakeIncidentStore(),
            audit ?? new FakeAuditLogStore(),
            new FakeSourceGovernanceBackend(),
            new FakeGrantStore(),
            runRepo ?? new FakeRunRepository(),
            new RunReportExporter(runRepo ?? new FakeRunRepository(), new FakeClock(now)),
            new FakeAuditIntegrityMonitor(),
            new FakeDiagnosticLogDirectory { Directory = diagDir, BaseName = "diag" },
            new FakeAppInfo(),
            canaryTokens,
            new FakeClock(now),
            matcher: matcher);

    [Fact]
    public async Task BuildAsync_Normal_WritesZipWithManifest()
    {
        var now = new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);
        var diagDir = Path.Combine(Path.GetTempPath(), "sb-norm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(diagDir);
        var outPath = Path.Combine(Path.GetTempPath(), "sb-norm-" + Guid.NewGuid().ToString("N") + ".zip");

        var builder = MakeBuilder(diagDir, new HashSet<string>(), now);
        var req = new SupportBundleRequest(outPath,
            new HashSet<SupportPackageCategory> { SupportPackageCategory.Configuration, SupportPackageCategory.SourceHealth },
            Array.Empty<RunId>());

        try
        {
            var res = await builder.BuildAsync(req);
            Assert.True(res.IsSuccess, res.IsSuccess ? "" : res.Error?.Code);

            Assert.True(File.Exists(outPath));
            Assert.Contains("Diagnostics", res.Value!.IncludedCategories);
            Assert.Contains("Configuration", res.Value.IncludedCategories);
            Assert.Contains("SourceHealth", res.Value.IncludedCategories);
            Assert.Equal(64, res.Value.ManifestHash.Length);
            Assert.True(res.Value.TotalBytes <= Limits.V1_5.SupportBundleMaxBytes);

            using var zip = ZipFile.OpenRead(outPath);
            Assert.Contains(zip.Entries, e => e.FullName == "manifest.json");
            Assert.Contains(zip.Entries, e => e.FullName == "meta.json");
        }
        finally
        {
            if (File.Exists(outPath)) File.Delete(outPath);
            if (Directory.Exists(diagDir)) Directory.Delete(diagDir, true);
        }
    }

    [Fact]
    public async Task BuildAsync_TooLarge_FailsAndDoesNotPublish()
    {
        var now = new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);
        var diagDir = Path.Combine(Path.GetTempPath(), "sb-big-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(diagDir);
        var outPath = Path.Combine(Path.GetTempPath(), "sb-big-" + Guid.NewGuid().ToString("N") + ".zip");

        // Note: diagnostics are re-redacted and truncated to Limits.V1_5.MaxRedactionStringLength (2000
        // chars) per file, so they can never exceed the 25 MiB cap on their own. The cap is reachable
        // only via the non-redacted categories (here: AuditLog). Seed an *incompressible* payload: a
        // string of random full-range BMP chars (lone surrogates excluded) so Deflate cannot shrink it
        // below the 25 MiB cap. (A base64 string of random bytes would compress ~25% and stay under cap.)
        var rnd = new Random(42);
        var sb = new StringBuilder(12_000_000);
        for (var i = 0; i < 12_000_000; i++) sb.Append((char)rnd.Next(0x20, 0xD800));
        var big = sb.ToString(); // ~35 MiB of incompressible UTF-8

        var audit = new FakeAuditLogStore();
        await audit.AppendAsync(new AuditEntry(
            1, now, AuditCategory.System, "seed", "tester", "{}", "{}", big, "prev", "hmac", now),
            CancellationToken.None);

        var builder = MakeBuilder(diagDir, new HashSet<string>(), now, audit: audit);
        var req = new SupportBundleRequest(outPath,
            new HashSet<SupportPackageCategory> { SupportPackageCategory.AuditLog },
            Array.Empty<RunId>());

        try
        {
            var res = await builder.BuildAsync(req);
            Assert.False(res.IsSuccess);
            Assert.Equal("RET_PKG_TOO_LARGE", res.Error!.Code);
            Assert.False(File.Exists(outPath), "oversized package must never be published");
        }
        finally
        {
            if (File.Exists(outPath)) File.Delete(outPath);
            if (Directory.Exists(diagDir)) Directory.Delete(diagDir, true);
        }
    }

    [Fact]
    public async Task BuildAsync_CanaryHit_FailsAndDoesNotPublish()
    {
        var now = new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);
        var diagDir = Path.Combine(Path.GetTempPath(), "sb-canary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(diagDir);

        var canary = "CANARY_TOKEN_XYZ123";
        await File.WriteAllTextAsync(Path.Combine(diagDir, "diag-canary.log"), "leak " + canary + " here");

        var outPath = Path.Combine(Path.GetTempPath(), "sb-canary-" + Guid.NewGuid().ToString("N") + ".zip");

        var builder = MakeBuilder(diagDir, new HashSet<string> { canary }, now);
        var req = new SupportBundleRequest(outPath,
            new HashSet<SupportPackageCategory> { SupportPackageCategory.Configuration },
            Array.Empty<RunId>());

        try
        {
            var res = await builder.BuildAsync(req);
            Assert.False(res.IsSuccess);
            Assert.Equal("RET_PKG_CANARY", res.Error!.Code);
            Assert.False(File.Exists(outPath), "package containing a canary must never be published");
        }
        finally
        {
            if (File.Exists(outPath)) File.Delete(outPath);
            if (Directory.Exists(diagDir)) Directory.Delete(diagDir, true);
        }
    }

    [Fact]
    public async Task BuildAsync_ProfileCanary_FailsAndDoesNotPublish()
    {
        // The canary set must come from a real SecretScanningProfile (not an empty HashSet) so the
        // production dead link is proven live: a surviving canary fails the build.
        var now = new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);
        var diagDir = Path.Combine(Path.GetTempPath(), "sb-pcan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(diagDir);

        var key = new byte[32];
        for (var i = 0; i < key.Length; i++) key[i] = (byte)(i + 1);
        var profile = new SecretScanningProfile(key, new HashSet<string> { "WP_CANY_PROFILE_TOKEN_42" });
        Assert.Single(profile.CanaryTokens);

        await File.WriteAllTextAsync(Path.Combine(diagDir, "diag-pcan.log"), "leak WP_CANY_PROFILE_TOKEN_42 here");

        var outPath = Path.Combine(Path.GetTempPath(), "sb-pcan-" + Guid.NewGuid().ToString("N") + ".zip");
        var builder = MakeBuilder(diagDir, profile.CanaryTokens, now, matcher: profile.BuildMatcher(Array.Empty<string>()));
        var req = new SupportBundleRequest(outPath,
            new HashSet<SupportPackageCategory> { SupportPackageCategory.Configuration },
            Array.Empty<RunId>());

        try
        {
            var res = await builder.BuildAsync(req);
            Assert.False(res.IsSuccess);
            Assert.Equal("RET_PKG_CANARY", res.Error!.Code);
            Assert.False(File.Exists(outPath), "package containing a profile canary must never be published");
        }
        finally
        {
            if (File.Exists(outPath)) File.Delete(outPath);
            if (Directory.Exists(diagDir)) Directory.Delete(diagDir, true);
        }
    }

    [Fact]
    public async Task BuildAsync_ProfileMatcher_RedactsKnownSecretAndSucceeds()
    {
        // Stage 3 (known-secret HMAC) must be live through the profile: a registered secret embedded in
        // diagnostics is redacted (not leaked) and the bundle still publishes successfully.
        var now = new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);
        var diagDir = Path.Combine(Path.GetTempPath(), "sb-pmat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(diagDir);

        var secret = "AKIA-SUPER-SECRET-VALUE-9988";
        var key = new byte[32];
        for (var i = 0; i < key.Length; i++) key[i] = (byte)(i + 7);
        var profile = new SecretScanningProfile(key, new HashSet<string>()); // no canary; matcher-only
        var matcher = profile.BuildMatcher(new[] { secret });

        await File.WriteAllTextAsync(Path.Combine(diagDir, "diag-pmat.log"), "token=" + secret + " end");

        var outPath = Path.Combine(Path.GetTempPath(), "sb-pmat-" + Guid.NewGuid().ToString("N") + ".zip");
        var builder = MakeBuilder(diagDir, profile.CanaryTokens, now, matcher: matcher);
        var req = new SupportBundleRequest(outPath,
            new HashSet<SupportPackageCategory> { SupportPackageCategory.Configuration },
            Array.Empty<RunId>());

        try
        {
            var res = await builder.BuildAsync(req);
            Assert.True(res.IsSuccess, res.IsSuccess ? "" : res.Error?.Code);
            Assert.True(File.Exists(outPath));

            using var zip = ZipFile.OpenRead(outPath);
            var diagEntry = zip.Entries.First(e => e.FullName.StartsWith("diagnostics/", StringComparison.Ordinal));
            using var reader = new StreamReader(diagEntry.Open());
            var content = await reader.ReadToEndAsync();
            Assert.Contains("[REDACTED:secret]", content);
            Assert.DoesNotContain(secret, content, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(outPath)) File.Delete(outPath);
            if (Directory.Exists(diagDir)) Directory.Delete(diagDir, true);
        }
    }
}

public class SecretScanningProfileTests
{
    private static byte[] FixedKey() { var k = new byte[32]; for (var i = 0; i < k.Length; i++) k[i] = (byte)(i * 3 + 1); return k; }

    [Fact]
    public void BuildMatcher_RedactsKnownSecret_Stage3Live()
    {
        var key = FixedKey();
        var secret = "sk-live-1a2b3c4d5e6f";
        var profile = new SecretScanningProfile(key, new HashSet<string>());
        var matcher = profile.BuildMatcher(new[] { secret });

        var result = RedactionPipeline.RedactSerialized("auth=sk-live-1a2b3c4d5e6f&x=1", matcher, null, releaseMode: true);
        Assert.True(result.RedactionCount >= 1);
        Assert.DoesNotContain(secret, result.Value, StringComparison.Ordinal);
        Assert.Contains("[REDACTED:secret]", result.Value);
        Assert.False(result.HasViolation);
    }

    [Fact]
    public void BuildMatcher_UnknownSecret_NotRedacted()
    {
        var key = FixedKey();
        var profile = new SecretScanningProfile(key, new HashSet<string>());
        var matcher = profile.BuildMatcher(new[] { "registered-secret" });

        var result = RedactionPipeline.RedactSerialized("value=not-registered", matcher, null, releaseMode: true);
        Assert.Equal(0, result.RedactionCount);
        Assert.Equal("value=not-registered", result.Value);
    }

    [Fact]
    public void CanaryTokens_AreExposedAndDistinctFromMatcher()
    {
        // Design invariant (LOG-A05): canaries feed Stage 7 only and must never be silently redacted
        // by Stage 3. They are exposed via CanaryTokens, not folded into the matcher's fingerprints.
        var key = FixedKey();
        var canary = "WP_CANY_INVARIANT_99";
        var profile = new SecretScanningProfile(key, new HashSet<string> { canary });

        Assert.Contains(canary, profile.CanaryTokens);
        var matcher = profile.BuildMatcher(Array.Empty<string>());
        var result = RedactionPipeline.RedactSerialized("x=" + canary, matcher, profile.CanaryTokens, releaseMode: true);
        // Stage 3 (matcher) leaves the canary untouched; Stage 7 (canary) hard-fails.
        Assert.True(result.HasViolation);
        Assert.Contains("RUN_REDACTION_CANARY", result.ViolationCodes);
    }
}
