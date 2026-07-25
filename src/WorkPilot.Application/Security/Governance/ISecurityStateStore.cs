using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Contracts.Primitives;

namespace WorkPilot.Application.Security.Governance;

/// <summary>
/// Tiny key/value security-state store (doc 06 §6.4 emergency_stop flag, detection health, etc.).
/// Persisted by Infrastructure (Migration 021 <c>security_state</c>); host provides the real
/// implementation. The store is display-name-free and holds only governance flags — never secrets.
/// </summary>
public interface ISecurityStateStore
{
    Task<Result> SetAsync(string key, string value, CancellationToken ct = default);
    Task<Result<string?>> GetAsync(string key, CancellationToken ct = default);
}
