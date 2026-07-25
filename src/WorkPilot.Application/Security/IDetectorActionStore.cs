using System.Threading;
using System.Threading.Tasks;

namespace WorkPilot.Application.Security;

/// <summary>
/// Records which detector actions have already been applied, keyed by the deterministic
/// <see cref="WorkPilot.Domain.Security.Detectors.DetectorAction.ActionId"/>. Enforces idempotency so a
/// recurring condition never re-fires the same remediation (doc 06 §4 "动作必须幂等").
/// </summary>
public interface IDetectorActionStore
{
    /// <summary>Returns true only the first time <paramref name="actionId"/> is seen.</summary>
    Task<bool> TryMarkAppliedAsync(string actionId, CancellationToken ct);
}
