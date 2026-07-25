using System.Threading;
using System.Threading.Tasks;

namespace WorkPilot.Host.Core.Scheduling;

/// <summary>
/// Resolves the current user's security identifier. The Windows implementation calls the OS
/// (LookupAccountName / WindowsIdentity); tests supply a stub. The SID is used both as the task
/// principal (so it runs only for this user) and as the ACL allow-entry (SID ACL signal, T08).
/// </summary>
public interface ISidResolver
{
    Task<string> ResolveCurrentUserSidAsync(CancellationToken cancellationToken = default);
}
