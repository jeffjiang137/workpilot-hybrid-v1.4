using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation;
using WorkPilot.Domain.Automation.Run;
using WorkPilot.Domain.Automation.Run.Interpreter;
using WorkPilot.Application.Automation.Run.Permit;

namespace WorkPilot.Application.Automation.Run.Executors;

/// <summary>
/// Executes a <c>capability_call</c> node with the Native single-use Permit threaded through the adapter
/// (T12, RUN-004 / PER-006 / PER-007). Flow: prepared → permit_issued → request_sending →
/// response_received → persisted. The adapter MUST consume the permit as its first I/O gate; any
/// permit failure (none / duplicate / epoch / lease / cancel) means no I/O occurs. The idempotency key
/// is <c>SHA256(run_id + node_id + logical_execution)</c> so a provider that supports idempotency can
/// safely retry a crash during <c>request_sending</c> (doc 04 §9).
/// </summary>
public sealed class CapabilityExecutor
{
    private readonly IPermitIssuer _permitIssuer;
    private readonly ICapabilityAdapterResolver _adapterResolver;
    private readonly ISideEffectJournal _journal;
    private readonly IClock _clock;
    private readonly Func<long> _revocationEpoch;

    public CapabilityExecutor(IPermitIssuer permitIssuer, ICapabilityAdapterResolver adapterResolver,
        ISideEffectJournal journal, IClock clock, Func<long>? revocationEpochProvider = null)
    {
        _permitIssuer = permitIssuer ?? throw new ArgumentNullException(nameof(permitIssuer));
        _adapterResolver = adapterResolver ?? throw new ArgumentNullException(nameof(adapterResolver));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _revocationEpoch = revocationEpochProvider ?? (() => 0);
    }

    public NodeEffectResult ExecuteNode(WorkflowNode node, VariableStore inputVars, AutomationRun run, StepRun step, CancellationToken ct)
        => ExecuteAsync(node, inputVars, run, step, ct).GetAwaiter().GetResult();

    private async System.Threading.Tasks.Task<NodeEffectResult> ExecuteAsync(
        WorkflowNode node, VariableStore inputVars, AutomationRun run, StepRun step, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            // Dry-run (RUN-005 / AUT-A11): never issue a permit or touch any adapter. Produce a
            // plan summary describing the would-be invocation and return Succeeded so the planner
            // walks the rest of the workflow. The per-run fake adapter's InvokeAsync is guaranteed
            // never to be called, so any test observing send count == 0 proves no I/O occurred.
            if (run.IsDryRun)
                return new NodeEffectResult(StepRunStatus.Succeeded, OutputKey: "plan", OutputValue: BuildDryRunPlan(node, run, step));

            if (node.Payload is null || node.Payload["capability"] is not JsonObject cap)
                return Failed(node.NodeId, RunErrors.CapabilityInvokeFailedError(node.NodeId, "missing_capability"));
            var sourceKind = AsString(cap, "source_kind");
            var sourceId = AsString(cap, "source_id");
            var stableId = AsString(cap, "stable_id");
            var schemaSha256 = AsString(cap, "schema_sha256");
            var risk = AsString(cap, "risk");
            if (sourceKind is null || sourceId is null || stableId is null)
                return Failed(node.NodeId, RunErrors.CapabilityInvokeFailedError(node.NodeId, "capability_identity"));

            var arguments = node.Payload["arguments"] ?? new JsonObject();
            var argumentJson = arguments.ToJsonString();
            var argumentDigest = Sha256(argumentJson);

            var idempotencyKey = Sha256($"{run.Id.Value}|{node.NodeId}|{step.LogicalExecution}");
            var idempotency = new IdempotencyContext(idempotencyKey, false);

            var validated = new ValidatedArguments(argumentJson, schemaSha256 ?? string.Empty);

            Record(run, step, SideEffectPhase.Prepared, null);

            // 1) Issue a single-use Native permit for this approved invocation.
            var invocation = new ApprovedInvocation(
                RunId: run.Id.Value,
                StepId: step.Id.Value,
                Attempt: step.Attempt,
                CapabilitySourceKind: sourceKind,
                CapabilitySourceId: sourceId,
                CapabilityStableId: stableId,
                SchemaSha256: schemaSha256 ?? string.Empty,
                ArgumentDigest: argumentDigest,
                RevocationEpoch: _revocationEpoch(),
                WorkerLeaseOwner: run.LeaseOwner ?? string.Empty,
                LeaseExpiresAtUtc: run.LeaseExpiresAtUtc ?? DateTimeOffset.MaxValue);

            var permitResult = await _permitIssuer.AcquirePermitAsync(invocation, ct).ConfigureAwait(false);
            if (!permitResult.IsSuccess)
                return Failed(node.NodeId, permitResult.Error!);
            Record(run, step, SideEffectPhase.PermitIssued, null);

            using var lease = permitResult.Value!;

            // 2) Resolve the adapter. Missing => capability not allowlisted => BlockedPolicy.
            var adapter = _adapterResolver.Resolve(sourceKind, sourceId, stableId);
            if (adapter is null)
                return new NodeEffectResult(StepRunStatus.BlockedPolicy,
                    ErrorCode: RunErrors.CapabilityNotFoundError(node.NodeId, stableId).Code);

            // 3) Live, per-run state for the send-time current-state check (doc 07 §11).
            lease.SetLiveState(new PermitLiveState(
                WorkerLeaseOwner: run.LeaseOwner ?? string.Empty,
                LeaseExpiresAtUtc: run.LeaseExpiresAtUtc ?? DateTimeOffset.MaxValue,
                CancellationRequested: run.CancellationRequestedAtUtc.HasValue));

            Record(run, step, SideEffectPhase.RequestSending, null);

            // 4) The adapter consumes the permit as its first I/O gate, then performs the side effect.
            var outcome = await adapter.InvokeAsync(validated, lease, idempotency, ct).ConfigureAwait(false);
            if (!outcome.IsSuccess)
            {
                // Surface the adapter's exact (permit / provider) failure code so recovery can decide.
                return Failed(node.NodeId, outcome.Error!);
            }

            Record(run, step, SideEffectPhase.ResponseReceived, null);
            Record(run, step, SideEffectPhase.Persisted, null);

            var bytes = outcome.Value!.ResultBytes;
            var summary = new JsonObject
            {
                ["success"] = outcome.Value.Success,
                ["result_bytes"] = bytes,
                ["capability"] = stableId
            };
            return new NodeEffectResult(StepRunStatus.Succeeded, OutputKey: "result", OutputValue: summary);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Unknown provider / config error: fail the step; never auto-classify as success.
            return Failed(node.NodeId, RunErrors.CapabilityInvokeFailedError(node.NodeId, ex.Message));
        }
    }

    private static string? AsString(JsonObject obj, string key)
        => obj.TryGetPropertyValue(key, out var v) && v is JsonValue jv ? jv.GetValue<string>() : null;

    private static string Sha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>Builds the dry-run plan summary for a <c>capability_call</c> node (no I/O performed).</summary>
    private static JsonObject BuildDryRunPlan(WorkflowNode node, AutomationRun run, StepRun step)
    {
        var cap = node.Payload?["capability"] as JsonObject;
        var sourceKind = cap?["source_kind"]?.GetValue<string>();
        var sourceId = cap?["source_id"]?.GetValue<string>();
        var stableId = cap?["stable_id"]?.GetValue<string>();
        var risk = cap?["risk"]?.GetValue<string>();
        var arguments = node.Payload?["arguments"] ?? new JsonObject();
        var argumentDigest = Sha256(arguments.ToJsonString());
        var idempotencyKey = Sha256($"{run.Id.Value}|{node.NodeId}|{step.LogicalExecution}");
        return new JsonObject
        {
            ["dry_run"] = true,
            ["node_kind"] = "capability_call",
            ["source_kind"] = (JsonNode?)sourceKind,
            ["source_id"] = (JsonNode?)sourceId,
            ["capability_stable_id"] = (JsonNode?)stableId,
            ["risk"] = (JsonNode?)risk,
            ["argument_digest"] = argumentDigest,
            ["would_be_idempotency_key"] = idempotencyKey,
            ["would_send"] = true
        };
    }

    private void Record(AutomationRun run, StepRun step, SideEffectPhase phase, string? detail)
        => _journal.Record(new SideEffectPhaseRecord(run.Id.Value, step.Id.Value, phase, _clock.UtcNow, detail));

    private static NodeEffectResult Failed(string nodeId, AppError error)
        => new(StepRunStatus.Failed, ErrorCode: error.Code);
}
