using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Contracts.Primitives;

namespace WorkPilot.Application.Security.Governance;

/// <summary>
/// Host-provided backend that actually toggles connectors / MCP servers and runs health probes
/// (doc 06 §7). The Security Center must not talk to connectors/MCP directly — it goes through this
/// port so the governance command stays a pure orchestration. Implemented by the host (WinUI/App).
/// </summary>
public interface ISourceGovernanceBackend
{
    /// <summary>Enable/disable a source. Disabling must terminate any process/session for MCP.</summary>
    Task<Result> SetSourceEnabledAsync(string sourceKind, string sourceId, bool enabled, CancellationToken ct = default);
    /// <summary>Terminate an in-flight server/process for the source (best-effort).</summary>
    Task<Result> TerminateAsync(string sourceKind, string sourceId, CancellationToken ct = default);
    /// <summary>Current health of all known sources (read-only probe; never exposes business bodies).</summary>
    Task<Result<IReadOnlyList<SourceHealth>>> ListHealthAsync(CancellationToken ct = default);
}
