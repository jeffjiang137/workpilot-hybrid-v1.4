using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Contracts.Primitives;

namespace WorkPilot.Application.Security.Retention;

/// <summary>
/// Manual database shrink (doc 05 §9): <c>VACUUM INTO</c> a temp file followed by atomic replace.
/// Never invoked by automatic cleanup. Host-provided (needs the live connection + file path).
/// </summary>
public interface IOptimizeDatabase
{
    Task<Result> OptimizeAsync(CancellationToken ct = default);
}
