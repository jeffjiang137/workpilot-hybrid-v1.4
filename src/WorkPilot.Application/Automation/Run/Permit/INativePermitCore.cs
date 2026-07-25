using WorkPilot.Contracts.Primitives;

namespace WorkPilot.Application.Automation.Run.Permit;

/// <summary>
/// Abstraction over the Native single-use Permit registry (ADR-1508). The sandbox uses
/// <see cref="ManagedPermitCore"/>; the Host later backs this with P/Invoke to
/// <c>wp_permit_issue</c> / <c>wp_permit_consume_and_check</c>. C# code can never forge a permit because
/// the signing key lives inside the core and only <see cref="Issue"/> returns a handle.
/// </summary>
public interface INativePermitCore
{
    /// <summary>Issue a single-use permit bound to <paramref name="binding"/> (native <c>wp_permit_issue</c>).</summary>
    IExecutionPermit Issue(PermitBinding binding);

    /// <summary>
    /// Atomically consume and verify the permit (native <c>wp_permit_consume_and_check</c>). On any
    /// failure the permit is left unconsumed and the caller must NOT perform I/O.
    /// </summary>
    Result<PermitConsumption> ConsumeAndCheck(string permitId, string signature, PermitBinding binding, PermitLiveState live);

    /// <summary>Revoke an un-consumed permit (e.g. lease disposed before send).</summary>
    void Revoke(string permitId);

    /// <summary>Current global/source revocation epoch. Security commands bump this; a permit whose
    /// binding epoch differs fails consume (doc 07 §11).</summary>
    long CurrentRevocationEpoch { get; set; }
}
