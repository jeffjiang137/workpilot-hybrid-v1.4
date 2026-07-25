using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Application.Diagnostics;
using WorkPilot.Application.Security.Retention;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Domain.Automation.Run.Redaction;
using Xunit;

namespace WorkPilot.Application.Tests.Diagnostics;

public class JsonlDiagnosticLoggerTests
{
    private static SecretFingerprint Fp(byte[] key, string secret)
    {
        using var h = new HMACSHA256(key);
        var digest = h.ComputeHash(Encoding.UTF8.GetBytes(secret));
        return new SecretFingerprint(Convert.ToBase64String(digest), Encoding.UTF8.GetBytes(secret).Length);
    }

    [Fact]
    public async Task Low_severity_dropped_under_backpressure_with_summary_LOG_A06()
    {
        // reader is ON but blocked on the sink, so the bounded channel fills and low events are dropped + counted
        var sink = new MemoryLogSink { Block = new ManualResetEventSlim(false) };
        var logger = new JsonlDiagnosticLogger(sink, channelCapacity: 10);

        for (var i = 0; i < 200; i++)
            logger.Emit(new DiagnosticEvent("TraceEvt", DiagnosticLogLevel.Trace, "-"));

        Assert.True(logger.DroppedLowCount > 0);
        Assert.Empty(sink.Lines); // reader blocked, nothing written yet

        sink.Block.Set();
        await logger.FlushAsync(TimeSpan.FromSeconds(2));

        // invariant: written + dropped == emitted (bounded discard, never lost silently without counting)
        Assert.Equal(200, sink.Lines.Count + logger.DroppedLowCount);
        // while the reader was blocked it held 1 in-flight item; on release it also flushes the buffered ones,
        // so the number written is bounded by channelCapacity + 1 (the single in-flight item).
        Assert.True(sink.Lines.Count <= 10 + 1);
        logger.Dispose();
    }

    [Fact]
    public async Task Warning_and_error_are_never_dropped()
    {
        var sink = new MemoryLogSink();
        var logger = new JsonlDiagnosticLogger(sink, channelCapacity: 5);

        for (var i = 0; i < 200; i++)
        {
            logger.Emit(new DiagnosticEvent("WarnEvt", DiagnosticLogLevel.Warning, "-"));
            logger.Emit(new DiagnosticEvent("ErrEvt", DiagnosticLogLevel.Error, "-"));
        }

        await logger.FlushAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, logger.DroppedLowCount); // high severity bypasses the bounded channel
        Assert.Equal(400, sink.Lines.Count);
        logger.Dispose();
    }

    [Fact]
    public void Io_failure_sets_degraded_and_never_throws_LOG_A09()
    {
        var sink = new MemoryLogSink { FailNextWrite = true };
        var logger = new JsonlDiagnosticLogger(sink, channelCapacity: 5);

        // Warning goes through the direct-write path -> sink throws -> degraded, no exception escapes
        var ex = Record.Exception(() => logger.Emit(new DiagnosticEvent("WarnEvt", DiagnosticLogLevel.Warning, "-")));
        Assert.Null(ex);
        Assert.True(logger.IsDegraded);
        Assert.Empty(sink.Lines);
        logger.Dispose();
    }

    [Fact]
    public async Task Canary_in_diagnostic_safe_object_is_redacted_LOG_A05()
    {
        var key = Encoding.UTF8.GetBytes("lease-key-0123456789");
        var canary = "CANARY-diag-plain";
        var matcher = new KnownSecretMatcher(key, new[] { Fp(key, canary) });
        var sink = new MemoryLogSink();
        var logger = new JsonlDiagnosticLogger(sink, matcher, channelCapacity: 5);

        logger.Emit(new DiagnosticEvent("DiagEvt", DiagnosticLogLevel.Warning, "-",
            new Dictionary<string, object?> { ["note"] = "value=" + canary }));

        await logger.FlushAsync(TimeSpan.FromSeconds(2));
        var line = sink.Lines.Count == 1 ? sink.Lines[0] : throw new Exception("expected 1 line");
        Assert.DoesNotContain(canary, line);
        Assert.Contains("[REDACTED:secret]", line);
        logger.Dispose();
    }

    [Fact]
    public async Task App_and_host_write_to_independent_sinks_LOG_A08()
    {
        var appSink = new MemoryLogSink();
        var hostSink = new MemoryLogSink();
        var appLogger = new JsonlDiagnosticLogger(appSink, channelCapacity: 20);
        var hostLogger = new JsonlDiagnosticLogger(hostSink, channelCapacity: 20);

        for (var i = 0; i < 10; i++)
        {
            appLogger.Emit(new DiagnosticEvent("AppEvt", DiagnosticLogLevel.Information, "-"));
            hostLogger.Emit(new DiagnosticEvent("HostEvt", DiagnosticLogLevel.Information, "-"));
        }

        await appLogger.FlushAsync(TimeSpan.FromSeconds(2));
        await hostLogger.FlushAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(10, appSink.Lines.Count);
        Assert.Equal(10, hostSink.Lines.Count);
        Assert.All(appSink.Lines, l => Assert.Contains("AppEvt", l));
        Assert.All(hostSink.Lines, l => Assert.Contains("HostEvt", l));
        appLogger.Dispose();
        hostLogger.Dispose();
    }

    [Fact]
    public async Task Rotation_trims_oldest_when_over_size_LOG_A08()
    {
        var sink = new MemoryLogSink { ForcedMaxBytes = 120 };
        var logger = new JsonlDiagnosticLogger(sink, channelCapacity: 20, maxBytes: 120);

        for (var i = 0; i < 40; i++)
            logger.Emit(new DiagnosticEvent("Evt", DiagnosticLogLevel.Warning, "-",
                new Dictionary<string, object?> { ["n"] = i }));

        await logger.FlushAsync(TimeSpan.FromSeconds(2));
        Assert.True(sink.RotateCallCount > 0);
        Assert.True(sink.Lines.Count < 40); // oldest trimmed on rotation
        logger.Dispose();
    }

    [Fact]
    public async Task SecretScanningProfile_redacts_secret_and_fails_canary_in_release_mode_T14()
    {
        // Mirrors the production wiring in AppServices.CreateAsync: one stable scanning key +
        // canary set feeds both the Stage-3 known-secret matcher and the Stage-7 canary gate.
        var key = Encoding.UTF8.GetBytes("stable-scanning-key-0123456789abcdef");
        var canary = "WP_CANY_T14_PROBE_XZ";
        var profile = new SecretScanningProfile(key, new[] { canary });

        var knownSecret = "sk-SUPER-SECRET-VALUE-123456";
        var matcher = profile.BuildMatcher(new[] { knownSecret });

        var sink = new MemoryLogSink();
        var logger = new JsonlDiagnosticLogger(sink, matcher, profile.CanaryTokens, releaseMode: true);

        // A registered secret in a plain (non-URL) field: Stage-3 HMAC must redact it.
        // (URL-embedded secrets are caught earlier by Stage-4's query-strip; both remove it.)
        logger.Emit(new DiagnosticEvent("RunEvent", DiagnosticLogLevel.Information, "-",
            new Dictionary<string, object?> { ["api_key"] = knownSecret }));
        // A canary in the safe bag must trip the strict release-mode failure marker (Stage-7).
        logger.Emit(new DiagnosticEvent("RunEvent", DiagnosticLogLevel.Information, "-",
            new Dictionary<string, object?> { ["note"] = "leak " + canary }));

        await logger.FlushAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, sink.Lines.Count);

        var secretLine = sink.Lines[0];
        Assert.DoesNotContain(knownSecret, secretLine);
        Assert.Contains("[REDACTED:secret]", secretLine);

        var canaryLine = sink.Lines[1];
        Assert.DoesNotContain(canary, canaryLine);
        Assert.Contains("[REDACTION_FAILURE]", canaryLine);
        logger.Dispose();
    }
}
