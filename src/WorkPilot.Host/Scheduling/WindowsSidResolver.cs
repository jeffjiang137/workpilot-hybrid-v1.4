using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Host.Core.Scheduling;

namespace WorkPilot.Host.Scheduling;

/// <summary>
/// Resolves the current user's SID on Windows. Used as the task principal (so the scheduled task
/// runs only for this user) and as the sole ACL allow-entry (SID ACL signal). Windows-only.
/// </summary>
public sealed class WindowsSidResolver : ISidResolver
{
    public Task<string> ResolveCurrentUserSidAsync(CancellationToken cancellationToken = default)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value
                  ?? throw new InvalidOperationException("Unable to resolve the current user SID.");
        return Task.FromResult(sid);
    }
}
