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

public sealed class DetectorEngineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 5, 2, 0, 0, 0, TimeSpan.Zero);
    private static readonly SourceReference Src = new("connector", "github");
    private static readonly IIdGenerator Ids = new SequentialIdGenerator();

    private static DetectorContext SampleCtx() => new(T0,
        new[] { new AuthFailureSample(Src, 5) },
        Array.Empty<McpSchemaChange>(),
        Array.Empty<McpProtocolViolation>(),
        Array.Empty<ExecutableHashChange>(),
        Array.Empty<OAuthMismatch>(),
        Array.Empty<DpapiFailure>(),
        Array.Empty<RedactionCanaryHit>(),
        Array.Empty<AuditIntegrityFailureSignal>(),
        Array.Empty<PolicyDenialSample>(),
        Array.Empty<WorkerCrashRecovery>(),
        Array.Empty<QueueBackpressure>(),
        new[] { new DiskSpaceLow(150) },
        Array.Empty<CapabilityNoPermit>(),
        Array.Empty<LeaseLostSendAttempt>(),
        Array.Empty<OutcomeUnknownWrite>(),
        Array.Empty<ApprovalRejectionBurst>());

    [Fact]
    public async Task Engine_runs_all_16_rules_and_emits_events_with_actions()
    {
        var rules = DetectorRuleCatalog.All(Ids);
        var emitter = new RecordingEmitter();
        var actions = new InMemoryDetectorActionStore();
        var executor = new RecordingExecutor();

        var engine = new DetectorEngine(rules, emitter, actions, executor);
        var result = await engine.RunAsync(SampleCtx(), CancellationToken.None);

        Assert.Contains(result.EmittedEvents, e => e.Type == SecurityEventType.AuthFailureContinuous);
        Assert.Contains(result.EmittedEvents, e => e.Type == SecurityEventType.DiskSpaceLow);
        Assert.Contains(result.AppliedActions, a => a.Kind == DetectorActionKind.DisableNewCalls);
        Assert.Contains(result.AppliedActions, a => a.Kind == DetectorActionKind.StopNewRuns);
        Assert.Equal(2, result.AppliedActions.Count);
    }

    [Fact]
    public async Task Engine_actions_are_idempotent_across_passes()
    {
        var rules = DetectorRuleCatalog.All(Ids);
        var emitter = new RecordingEmitter();
        var actions = new InMemoryDetectorActionStore();
        var executor = new RecordingExecutor();

        var engine = new DetectorEngine(rules, emitter, actions, executor);
        await engine.RunAsync(SampleCtx(), CancellationToken.None);
        var second = await engine.RunAsync(SampleCtx(), CancellationToken.None);

        // Events are still emitted each pass (the incident aggregator silences duplicates), but the
        // remediation actions are NOT re-applied because the action store already marked them.
        Assert.NotEmpty(second.EmittedEvents);
        Assert.Empty(second.AppliedActions);
        Assert.Equal(2, executor.AppliedCount); // applied exactly once
    }

    [Fact]
    public async Task Engine_does_not_double_emit_same_finding_within_one_pass()
    {
        var rules = DetectorRuleCatalog.All(Ids);
        var emitter = new RecordingEmitter();
        var actions = new InMemoryDetectorActionStore();
        var executor = new RecordingExecutor();

        var engine = new DetectorEngine(rules, emitter, actions, executor);
        await engine.RunAsync(SampleCtx(), CancellationToken.None);

        // Each triggered rule contributes exactly one event of its type.
        Assert.Single(emitter.Events, e => e.Type == SecurityEventType.AuthFailureContinuous);
        Assert.Single(emitter.Events, e => e.Type == SecurityEventType.DiskSpaceLow);
    }
}

internal sealed class InMemoryDetectorActionStore : IDetectorActionStore
{
    private readonly HashSet<string> _applied = new();
    public Task<bool> TryMarkAppliedAsync(string actionId, CancellationToken ct)
    {
        lock (_applied)
        {
            if (_applied.Contains(actionId)) return Task.FromResult(false);
            _applied.Add(actionId);
            return Task.FromResult(true);
        }
    }
}

internal sealed class RecordingExecutor : IDetectorActionExecutor
{
    public int AppliedCount { get; private set; }
    public Task<Result> ApplyAsync(DetectorAction action, CancellationToken ct)
    {
        AppliedCount++;
        return Task.FromResult(Result.Success());
    }
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
