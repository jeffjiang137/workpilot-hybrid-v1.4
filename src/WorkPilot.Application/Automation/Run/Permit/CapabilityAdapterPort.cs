using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Contracts.Primitives;

namespace WorkPilot.Application.Automation.Run.Permit;

/// <summary>Static descriptor of a capability an adapter can invoke (doc 09 §9).</summary>
public sealed record CapabilityDescriptor(
    string SourceKind,
    string SourceId,
    string StableId,
    string Title,
    string Risk,
    bool Mutating);

/// <summary>Arguments already validated against the capability's schema (doc 09 §9).</summary>
public sealed record ValidatedArguments(string Json, string SchemaSha256);

/// <summary>Idempotency context for a single capability send (doc 04 §9).</summary>
public sealed record IdempotencyContext(string Key, bool ProviderSupportsIdempotency);

/// <summary>Result returned by a capability adapter, safe to surface (no secrets, no raw error text).</summary>
public sealed record CapabilityResultSummary(bool Success, JsonNode? Output, string? ErrorCategory, long ResultBytes);

/// <summary>
/// Unified adapter entry point for a capability (doc 09 §9). The adapter MUST call
/// <paramref name="permit"/>.ConsumeAndCheckAsync() as its first I/O gate; if it returns failure the
/// adapter must not open a socket or write a pipe. A fake adapter in tests proves that without a valid
/// permit no I/O occurs (PER-A13, T12 DoD).
/// </summary>
public interface ICapabilityAdapter
{
    CapabilityDescriptor Descriptor { get; }

    Task<Result<CapabilityResultSummary>> InvokeAsync(
        ValidatedArguments arguments,
        ExecutionPermitLease permit,
        IdempotencyContext idempotency,
        CancellationToken ct);
}
