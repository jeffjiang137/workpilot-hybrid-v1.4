using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Domain.Security.Detectors;

namespace WorkPilot.Application.Security;

/// <summary>
/// Applies a detector's remediation action against the real system (disable source, pause automation,
/// stop worker, …). The actual side effects are wired in T20 governance commands; the T19 default
/// is a no-op recorder so detectors can be exercised end-to-end without the host.
/// </summary>
public interface IDetectorActionExecutor
{
    Task<Result> ApplyAsync(DetectorAction action, CancellationToken ct);
}
