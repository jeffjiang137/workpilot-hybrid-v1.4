using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Application.Security.Governance;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Models;

namespace WorkPilot.Services;

/// <summary>
/// Host backend that actually toggles connectors / MCP servers and probes their health (doc 06 §7).
/// The Security Center never talks to connectors or MCP directly — every source command goes through
/// this port so the governance command stays a pure orchestration in <see cref="SourceGovernanceService"/>.
/// Implemented here (the WinUI host) because it needs the live <see cref="ConnectorService"/> and
/// <see cref="McpService"/>. WinUI compilation is gated to a real Windows build (doc 10 §16).
/// </summary>
public sealed class SecuritySourceGovernanceBackend : ISourceGovernanceBackend
{
    private readonly ConnectorService _connectors;
    private readonly McpService _mcp;

    public SecuritySourceGovernanceBackend(ConnectorService connectors, McpService mcp)
    {
        _connectors = connectors ?? throw new ArgumentNullException(nameof(connectors));
        _mcp = mcp ?? throw new ArgumentNullException(nameof(mcp));
    }

    public async Task<Result> SetSourceEnabledAsync(string sourceKind, string sourceId, bool enabled, CancellationToken ct = default)
    {
        try
        {
            if (string.Equals(sourceKind, "connector", StringComparison.OrdinalIgnoreCase))
                await _connectors.SetEnabledAsync(sourceId, enabled, ct);
            else if (string.Equals(sourceKind, "mcp", StringComparison.OrdinalIgnoreCase))
                await _mcp.SetEnabledAsync(sourceId, enabled, ct);
            else
                return Result.Failure(InvalidSourceKind(sourceKind));
            return Result.Success();
        }
        catch (Exception error)
        {
            return Result.Failure(BackendError(sourceKind, sourceId, "set_enabled", error));
        }
    }

    public async Task<Result> TerminateAsync(string sourceKind, string sourceId, CancellationToken ct = default)
    {
        try
        {
            // Connector accounts are stateless API credentials — there is no in-flight process to
            // terminate. MCP servers hold a live session/process; SetEnabledAsync(false) disposes it.
            if (string.Equals(sourceKind, "mcp", StringComparison.OrdinalIgnoreCase))
                await _mcp.SetEnabledAsync(sourceId, false, ct);
            return Result.Success();
        }
        catch (Exception error)
        {
            return Result.Failure(BackendError(sourceKind, sourceId, "terminate", error));
        }
    }

    public async Task<Result<IReadOnlyList<SourceHealth>>> ListHealthAsync(CancellationToken ct = default)
    {
        var items = new List<SourceHealth>();
        var now = DateTimeOffset.UtcNow;

        try
        {
            var connectors = await _connectors.ListAsync(ct);
            foreach (var c in connectors)
            {
                try
                {
                    items.Add(ToHealth("connector", c.Id, c.State, c.LastErrorCode, c.LastSuccessAt, now));
                }
                catch
                {
                    items.Add(Unknown("connector", c.Id, now));
                }
            }
        }
        catch (Exception error)
        {
            // A total connector listing failure surfaces as detection-degraded — the provider must
            // NOT swallow it and must NOT present 0 sources as "safe" (doc 06 §10).
            return Result<IReadOnlyList<SourceHealth>>.Fail(BackendError("connector", "*", "list", error));
        }

        try
        {
            var servers = await _mcp.ListAsync(ct);
            foreach (var s in servers)
            {
                try
                {
                    var state = s.Enabled ? s.State : "disabled";
                    items.Add(ToHealth("mcp", s.Id, state, s.LastErrorCode, s.LastConnectedAt, now));
                }
                catch
                {
                    items.Add(Unknown("mcp", s.Id, now));
                }
            }
        }
        catch (Exception error)
        {
            return Result<IReadOnlyList<SourceHealth>>.Fail(BackendError("mcp", "*", "list", error));
        }

        return Result<IReadOnlyList<SourceHealth>>.Ok(items);
    }

    private static SourceHealth ToHealth(string kind, string id, string state, string? lastError, DateTimeOffset? lastSuccess, DateTimeOffset now)
    {
        var status = (state, lastError) switch
        {
            ("disabled", _) => SourceHealthStatus.Disabled,
            ("connected", _) => SourceHealthStatus.Healthy,
            ("running", _) => SourceHealthStatus.Healthy,
            ("degraded", _) => SourceHealthStatus.Degraded,
            ("expired", _) => SourceHealthStatus.Expired,
            ("error", _) => SourceHealthStatus.Degraded,
            ("starting", _) => SourceHealthStatus.Degraded,
            (_, not null) => SourceHealthStatus.Degraded,
            _ => SourceHealthStatus.Unknown
        };
        var detail = lastError is not null
            ? $"last_error={lastError}"
            : (status == SourceHealthStatus.Healthy ? null : state);
        return new SourceHealth(kind, id, status, detail, lastSuccess ?? now);
    }

    private static SourceHealth Unknown(string kind, string id, DateTimeOffset now) =>
        new(kind, id, SourceHealthStatus.Unknown, null, now);

    private static AppError InvalidSourceKind(string kind) =>
        new("SEC_SOURCE_KIND_UNSUPPORTED", ErrorCategory.Validation, "security.source.kind_unsupported", false,
            new Dictionary<string, string> { ["kind"] = kind });

    private static AppError BackendError(string kind, string id, string op, Exception error) =>
        new("SEC_BACKEND_FAILED", ErrorCategory.Internal, "security.backend.failed", false,
            new Dictionary<string, string> { ["kind"] = kind, ["id"] = id, ["op"] = op });
}
