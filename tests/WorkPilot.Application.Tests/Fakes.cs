using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Application.Automation.Run.Executors;
using WorkPilot.Application.Automation.Run.Permit;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation.Run;

namespace WorkPilot.Application.Tests;

internal sealed class FakeClock(DateTimeOffset fixedTime) : IClock
{
    public DateTimeOffset UtcNow => fixedTime;
    public DateTimeOffset Now => fixedTime;
}

/// <summary>Deterministic id generator producing sortable, stable ids for tests.</summary>
internal sealed class SequentialIdGenerator : IIdGenerator
{
    private int _counter;
    public string NewId() => $"id_{++_counter:000000}";
}

/// <summary>In-memory <see cref="INotificationSink"/> that records the last delivered content.</summary>
internal sealed class RecordingNotificationSink : INotificationSink
{
    public NotificationContent? Last { get; private set; }
    public bool ShouldDeliver { get; set; } = true;
    public int Calls { get; private set; }

    public Task<NotificationDeliveryResult> ShowAsync(NotificationContent content, CancellationToken ct)
    {
        Calls++;
        Last = content;
        return Task.FromResult(ShouldDeliver
            ? new NotificationDeliveryResult(true)
            : new NotificationDeliveryResult(false, "SINK_UNAVAILABLE"));
    }
}

/// <summary>Scripted <see cref="IAgentBackend"/> for executor tests.</summary>
internal sealed class ScriptedAgentBackend : IAgentBackend
{
    public AgentInvocationRequest? LastRequest;
    public bool ShouldCancel;
    public AgentInvocationResult NextResult = new(true, OutputValue: JsonValue.Create("ok"), ErrorCode: null);

    public Task<AgentInvocationResult> InvokeAsync(AgentInvocationRequest request, CancellationToken ct)
    {
        LastRequest = request;
        if (ShouldCancel || ct.IsCancellationRequested)
            throw new OperationCanceledException();
        return Task.FromResult(NextResult);
    }
}

/// <summary>
/// Fake <see cref="ICapabilityAdapter"/> that strictly consumes the permit as its first I/O gate.
/// <see cref="IoCalls"/> only increments after a successful consume, so any test observing
/// <c>IoCalls == 0</c> proves no external I/O occurred (PER-A13, T12 DoD).
/// </summary>
internal sealed class FakeCapabilityAdapter : ICapabilityAdapter
{
    public CapabilityDescriptor Descriptor { get; } = new("connector", "acct_1", "send_email", "Send Email", "medium", true);
    public int IoCalls;
    public bool LastConsumeSucceeded;
    public CapabilityResultSummary NextResult = new(true, JsonValue.Create("sent"), null, 12);

    public Task<Result<CapabilityResultSummary>> InvokeAsync(
        ValidatedArguments arguments, ExecutionPermitLease permit, IdempotencyContext idempotency, CancellationToken ct)
    {
        var consumed = permit.ConsumeAndCheckAsync(ct).GetAwaiter().GetResult();
        LastConsumeSucceeded = consumed.IsSuccess;
        if (!consumed.IsSuccess)
            return Task.FromResult(Result<CapabilityResultSummary>.Fail(consumed.Error!));
        IoCalls++; // first (and only) I/O happens strictly after a successful consume
        return Task.FromResult(Result<CapabilityResultSummary>.Ok(NextResult));
    }
}

/// <summary>Resolver returning a scripted adapter (or null to simulate a non-allowlisted capability).</summary>
internal sealed class FakeAdapterResolver : ICapabilityAdapterResolver
{
    public ICapabilityAdapter? Adapter;
    public ICapabilityAdapter? Resolve(string sourceKind, string sourceId, string stableId) => Adapter;
}

/// <summary>Builds AutomationRun fixtures with the lease/cancellation state a capability permit needs.</summary>
internal static class RunFakes
{
    public static AutomationRun CapabilityRun(
        DateTimeOffset now, string leaseOwner = "worker_a", DateTimeOffset? leaseExpiry = null, bool cancelled = false)
    {
        var run = AutomationRun.Create(
                RunId.Parse("run_1"),
                AutomationRevisionId.Parse("rev_1"),
                RunSnapshotId.Parse("snap_1"),
                RunTriggerKind.Interval, now, now)
            .MarkRunning(now)
            with { LeaseOwner = leaseOwner, LeaseExpiresAtUtc = leaseExpiry ?? now.AddHours(1) };
        return cancelled ? run with { CancellationRequestedAtUtc = now } : run;
    }

    public static StepRun DummyStep(string nodeId, string runId = "run_1") =>
        StepRun.Create(StepRunId.Create(new SequentialIdGenerator()), RunId.Parse(runId), nodeId, "capability_call",
            $"step:{nodeId}", $"digest:{nodeId}", 1, 1);
}

