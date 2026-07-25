using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Contracts.Primitives;

using WorkPilot.Host.Core.Health;

namespace WorkPilot.Host.Core.Scheduling;

/// <summary>
/// Logon type used when registering the background Host task. We always register a
/// per-user, non-elevated task (AI dev rule §16: "Host 只是 App 内 Timer，关闭 App 不运行" is
/// forbidden; the Host must be a real OS-scheduled, separate process).
/// </summary>
public enum HostLogonType
{
    /// <summary>Run only when the user is logged on, using the interactive token. No elevation, no stored password.</summary>
    InteractiveToken,
}

/// <summary>Trigger kind for the background Host task.</summary>
public enum HostTriggerKind
{
    /// <summary>Fire when the current user logs on.</summary>
    Logon,
    /// <summary>Fire on a fixed interval (belt-and-suspenders alongside the logon trigger).</summary>
    Interval,
}

/// <summary>A single trigger definition for the Host task.</summary>
public sealed record SchedulerTrigger(HostTriggerKind Kind, TimeSpan? Interval);

/// <summary>
/// A fully-described background Host scheduled task. Pure data: the Windows COM registrar
/// (WorkPilot.Host) materializes this into a real ITaskDefinition. Keeping it as data lets the
/// registration decision logic be unit-tested without COM.
/// </summary>
public sealed record HostTaskDescriptor(
    string TaskName,
    string ExecutablePath,
    string Arguments,
    string UserId,
    HostLogonType LogonType,
    IReadOnlyList<string> AllowedSids,
    IReadOnlyList<SchedulerTrigger> Triggers,
    string Description)
{
    /// <summary>SDDL allowing only <see cref="AllowedSids"/> to control the task. Null => default ACL.</summary>
    public string? SecurityDescriptorSddl
    {
        get
        {
            if (AllowedSids is null || AllowedSids.Count == 0)
                return null;
            // D:(A;;FA;;;SID) grants full control to each allowed SID and (implicitly) denies others.
            var aces = string.Join(string.Empty, AllowedSids.Select(s => $"(A;;FA;;;{s})"));
            return "D:" + aces;
        }
    }
}

/// <summary>The observable state of a registered Host task, as reported by the OS scheduler.</summary>
public enum HostTaskStatus
{
    Unknown,
    Registered,
    Running,
    Disabled,
    NotFound,
}

/// <summary>
/// Port to the OS task scheduler. The Windows implementation lives in <c>WorkPilot.Host</c>
/// (net8.0-windows, COM interop). The abstracted port lets the registration orchestration be
/// tested with an in-memory stub on any platform.
/// </summary>
public interface ITaskScheduler
{
    /// <summary>Register (or update) the Host task. Returns the resulting status.</summary>
    Task<Result<HostTaskStatus>> RegisterAsync(HostTaskDescriptor descriptor, CancellationToken cancellationToken = default);

    /// <summary>Query whether the Host task is already registered. Never throws on missing task.</summary>
    Task<Result<HostTaskStatus>> QueryAsync(string taskName, CancellationToken cancellationToken = default);

    /// <summary>Remove the Host task if present. Idempotent.</summary>
    Task<Result<bool>> RemoveAsync(string taskName, CancellationToken cancellationToken = default);

    /// <summary>Report scheduler-side health for the Host task (e.g. last run result).</summary>
    Task<Result<HostHealth>> GetHealthAsync(string taskName, CancellationToken cancellationToken = default);
}
