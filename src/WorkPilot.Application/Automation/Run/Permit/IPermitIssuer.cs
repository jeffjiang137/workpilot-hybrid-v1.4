using System;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Domain.Automation.Run;

namespace WorkPilot.Application.Automation.Run.Permit;

/// <summary>
/// An already-approved capability call awaiting a single-use permit (doc 07 §9). The policy decision is
/// assumed pre-validated by the caller; this bundles the binding context the Native Core seals into the
/// permit. <see cref="ApprovedInvocation.RevocationEpoch"/> is the epoch observed at approval time and is
/// re-checked at send time against <see cref="INativePermitCore.CurrentRevocationEpoch"/>.
/// </summary>
public sealed record ApprovedInvocation(
    string RunId,
    string StepId,
    int Attempt,
    string CapabilitySourceKind,
    string CapabilitySourceId,
    string CapabilityStableId,
    string SchemaSha256,
    string ArgumentDigest,
    long RevocationEpoch,
    string WorkerLeaseOwner,
    DateTimeOffset LeaseExpiresAtUtc,
    TimeSpan? Ttl = null);

/// <summary>Mints a Native single-use permit for an approved capability invocation.</summary>
public interface IPermitIssuer
{
    Task<Result<ExecutionPermitLease>> AcquirePermitAsync(ApprovedInvocation invocation, CancellationToken ct = default);
}

/// <summary>
/// Mints a Native single-use permit for an approved capability invocation. Mirrors the policy application
/// service requesting <c>wp_permit_issue</c> after a successful current-state check (doc 07 §9-10). The
/// permit is bound to the worker process nonce, run/step/attempt, capability/schema, argument digest,
/// revocation epoch and lease, and expires after 30 seconds (single-use, non-serializable).
/// </summary>
public sealed class PermitIssuer : IPermitIssuer
{
    private readonly INativePermitCore _core;
    private readonly IClock _clock;
    private readonly IIdGenerator _ids;
    private readonly string _processNonce;

    public PermitIssuer(INativePermitCore core, IClock clock, IIdGenerator ids)
    {
        _core = core ?? throw new ArgumentNullException(nameof(core));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
        _processNonce = Guid.NewGuid().ToString("N");
    }

    public Task<Result<ExecutionPermitLease>> AcquirePermitAsync(ApprovedInvocation inv, CancellationToken ct = default)
    {
        try
        {
            var binding = new PermitBinding(
                WorkerProcessNonce: _processNonce,
                InvocationId: _ids.NewId(),
                RunId: inv.RunId,
                StepId: inv.StepId,
                Attempt: inv.Attempt,
                CapabilitySourceKind: inv.CapabilitySourceKind,
                CapabilitySourceId: inv.CapabilitySourceId,
                CapabilityStableId: inv.CapabilityStableId,
                SchemaSha256: inv.SchemaSha256,
                ArgumentDigest: inv.ArgumentDigest,
                RevocationEpoch: inv.RevocationEpoch,
                WorkerLeaseOwner: inv.WorkerLeaseOwner,
                LeaseExpiresAtUtc: inv.LeaseExpiresAtUtc,
                ExpiresAtUtc: _clock.UtcNow.Add(inv.Ttl ?? TimeSpan.FromSeconds(30)));
            var permit = _core.Issue(binding);
            return Task.FromResult(Result<ExecutionPermitLease>.Ok(new ExecutionPermitLease(permit)));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result<ExecutionPermitLease>.Fail(
                RunErrors.PermitIssueFailedError(inv.StepId, ex.Message)));
        }
    }
}
