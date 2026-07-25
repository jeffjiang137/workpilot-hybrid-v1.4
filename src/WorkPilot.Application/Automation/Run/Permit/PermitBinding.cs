using System;

namespace WorkPilot.Application.Automation.Run.Permit;

/// <summary>
/// Immutable binding context sealed into a single-use Native Permit (ADR-1508, doc 07 §10). The permit
/// cannot be reconstructed by C# — only the Native Core mints it — and any I/O requires the permit to
/// be consumed against this exact binding under the current revocation epoch and worker lease.
/// </summary>
public sealed record PermitBinding(
    string WorkerProcessNonce,
    string InvocationId,
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
    DateTimeOffset ExpiresAtUtc);
