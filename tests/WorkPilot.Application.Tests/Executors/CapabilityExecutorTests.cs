using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using WorkPilot.Application.Automation.Run.Executors;
using WorkPilot.Application.Automation.Run.Permit;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation;
using WorkPilot.Domain.Automation.Run;
using WorkPilot.Domain.Automation.Run.Interpreter;
using Xunit;

namespace WorkPilot.Application.Tests.Executors;

/// <summary>Capability executor (T12): Native Permit gate, side-effect phases, crash-point matrix.</summary>
public class CapabilityExecutorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    private static WorkflowNode CapabilityNode(string id = "cap", string stableId = "send_email") =>
        new(id, id, "capability_call", 60, false, new JsonObject
        {
            ["capability"] = new JsonObject
            {
                ["source_kind"] = "connector",
                ["source_id"] = "acct_1",
                ["stable_id"] = stableId,
                ["schema_sha256"] = "sha256abc",
                ["risk"] = "medium"
            },
            ["arguments"] = new JsonObject { ["to"] = "a@b.c", ["body"] = "hi" }
        });

    private static CapabilityExecutor WiredExecutor(ManagedPermitCore core, ICapabilityAdapter? adapter, ISideEffectJournal journal, long epoch = 0)
    {
        var issuer = new PermitIssuer(core, new FakeClock(Now), new SequentialIdGenerator());
        var resolver = new FakeAdapterResolver { Adapter = adapter };
        return new CapabilityExecutor(issuer, resolver, journal, new FakeClock(Now), () => epoch);
    }

    [Fact]
    public void Capability_executes_and_consumes_permit_before_io()
    {
        var core = new ManagedPermitCore(new FakeClock(Now));
        core.CurrentRevocationEpoch = 0;
        var adapter = new FakeCapabilityAdapter();
        var journal = new InMemorySideEffectJournal();
        var exec = WiredExecutor(core, adapter, journal, epoch: 0);

        var step = RunFakes.DummyStep("cap");
        var result = exec.ExecuteNode(CapabilityNode(), new VariableStore(), RunFakes.CapabilityRun(Now), step, CancellationToken.None);

        Assert.Equal(StepRunStatus.Succeeded, result.Status);
        Assert.True(adapter.LastConsumeSucceeded);
        Assert.Equal(1, adapter.IoCalls);
        Assert.Equal("result", result.OutputKey);

        var phases = journal.EntriesFor("run_1", step.Id.Value).Select(e => e.Phase).ToList();
        Assert.Equal(new[]
        {
            SideEffectPhase.Prepared,
            SideEffectPhase.PermitIssued,
            SideEffectPhase.RequestSending,
            SideEffectPhase.ResponseReceived,
            SideEffectPhase.Persisted
        }, phases);
    }

    [Fact]
    public void Unknown_capability_is_blocked_policy_without_io()
    {
        var core = new ManagedPermitCore(new FakeClock(Now));
        core.CurrentRevocationEpoch = 0;
        var adapter = new FakeCapabilityAdapter();
        var journal = new InMemorySideEffectJournal();
        var exec = WiredExecutor(core, null, journal, epoch: 0);

        var result = exec.ExecuteNode(CapabilityNode(), new VariableStore(), RunFakes.CapabilityRun(Now), RunFakes.DummyStep("cap"), CancellationToken.None);

        Assert.Equal(StepRunStatus.BlockedPolicy, result.Status);
        Assert.Equal("RUN_CAPABILITY_NOT_FOUND", result.ErrorCode);
        Assert.Equal(0, adapter.IoCalls); // resolver returned null; adapter never reached
    }

    [Fact]
    public void Epoch_changed_before_send_fails_without_io()
    {
        var core = new ManagedPermitCore(new FakeClock(Now));
        core.CurrentRevocationEpoch = 7; // bumped after issue (binding epoch 0)
        var adapter = new FakeCapabilityAdapter();
        var journal = new InMemorySideEffectJournal();
        var exec = WiredExecutor(core, adapter, journal, epoch: 0);

        var result = exec.ExecuteNode(CapabilityNode(), new VariableStore(), RunFakes.CapabilityRun(Now), RunFakes.DummyStep("cap"), CancellationToken.None);

        Assert.Equal(StepRunStatus.Failed, result.Status);
        Assert.Equal("RUN_PERMIT_EPOCH", result.ErrorCode);
        Assert.Equal(0, adapter.IoCalls); // no I/O despite a valid-looking adapter
    }

    [Fact]
    public void Lease_expired_before_send_fails_without_io()
    {
        var core = new ManagedPermitCore(new FakeClock(Now));
        core.CurrentRevocationEpoch = 0;
        var adapter = new FakeCapabilityAdapter();
        var journal = new InMemorySideEffectJournal();
        var exec = WiredExecutor(core, adapter, journal, epoch: 0);

        var run = RunFakes.CapabilityRun(Now, leaseExpiry: Now.AddMinutes(-1)); // lease already expired
        var result = exec.ExecuteNode(CapabilityNode(), new VariableStore(), run, RunFakes.DummyStep("cap"), CancellationToken.None);

        Assert.Equal(StepRunStatus.Failed, result.Status);
        Assert.Equal("RUN_PERMIT_LEASE", result.ErrorCode);
        Assert.Equal(0, adapter.IoCalls);
    }

    [Fact]
    public void Cancellation_requested_before_send_fails_without_io()
    {
        var core = new ManagedPermitCore(new FakeClock(Now));
        core.CurrentRevocationEpoch = 0;
        var adapter = new FakeCapabilityAdapter();
        var journal = new InMemorySideEffectJournal();
        var exec = WiredExecutor(core, adapter, journal, epoch: 0);

        var run = RunFakes.CapabilityRun(Now, cancelled: true);
        var result = exec.ExecuteNode(CapabilityNode(), new VariableStore(), run, RunFakes.DummyStep("cap"), CancellationToken.None);

        Assert.Equal(StepRunStatus.Failed, result.Status);
        Assert.Equal("RUN_PERMIT_CANCELLED", result.ErrorCode);
        Assert.Equal(0, adapter.IoCalls);
    }

    [Fact]
    public void End_to_end_capability_node_succeeds_via_interpreter()
    {
        var core = new ManagedPermitCore(new FakeClock(Now));
        core.CurrentRevocationEpoch = 0;
        var adapter = new FakeCapabilityAdapter();
        var journal = new InMemorySideEffectJournal();
        var dispatcher = new NodeEffectExecutor(
            new ScriptedAgentBackend(), new RecordingNotificationSink(),
            new PermitIssuer(core, new FakeClock(Now), new SequentialIdGenerator()),
            new FakeAdapterResolver { Adapter = adapter },
            journal, new FakeClock(Now), () => 0);

        var wf = new WorkflowDefinition(1, "cap", new[] { CapabilityNode() }, Array.Empty<WorkflowEdge>());
        var budget = new RunBudget(10, 1_000_000, 10_000, 10, 1_000_000);
        var result = WorkflowInterpreter.Interpret(
            wf, RunFakes.CapabilityRun(Now), Array.Empty<StepRun>(), budget,
            new VariableStore(), dispatcher, new SequentialIdGenerator(), new FakeClock(Now), CancellationToken.None, false);

        Assert.Equal(RunStatus.Completed, result.Run.Status);
        var step = Assert.Single(result.Steps);
        Assert.Equal(StepRunStatus.Succeeded, step.Status);
        Assert.Equal(1, adapter.IoCalls);
    }

    [Fact]
    public void Dry_run_produces_plan_without_io_permit_or_journal()
    {
        // Real-looking wiring, but the run is flagged IsDryRun so the executor short-circuits.
        var core = new ManagedPermitCore(new FakeClock(Now));
        core.CurrentRevocationEpoch = 0;
        var adapter = new FakeCapabilityAdapter();
        var journal = new InMemorySideEffectJournal();
        var exec = WiredExecutor(core, adapter, journal, epoch: 0);

        var run = RunFakes.CapabilityRun(Now) with { IsDryRun = true };
        var result = exec.ExecuteNode(CapabilityNode(), new VariableStore(), run, RunFakes.DummyStep("cap"), CancellationToken.None);

        // No external side effect occurred: adapter I/O untouched, no side-effect phases recorded.
        Assert.Equal(StepRunStatus.Succeeded, result.Status);
        Assert.Equal("plan", result.OutputKey);
        Assert.Equal(0, adapter.IoCalls); // AUT-A11: High write generates a plan, never sends

        var plan = Assert.IsType<JsonObject>(result.OutputValue);
        Assert.Equal(true, plan["dry_run"]?.GetValue<bool>());
        Assert.Equal("send_email", plan["capability_stable_id"]?.GetValue<string>());
        Assert.Equal(true, plan["would_send"]?.GetValue<bool>());
        Assert.False(string.IsNullOrEmpty(plan["argument_digest"]?.GetValue<string>()));
        Assert.False(string.IsNullOrEmpty(plan["would_be_idempotency_key"]?.GetValue<string>()));
    }
}
