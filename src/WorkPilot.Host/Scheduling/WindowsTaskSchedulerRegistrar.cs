using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Host.Core.Health;
using WorkPilot.Host.Core.Scheduling;

namespace WorkPilot.Host.Scheduling;

/// <summary>
/// Windows implementation of <see cref="ITaskScheduler"/> using the Task Scheduler 2.0 COM API.
/// Registers a per-user, non-elevated, least-privilege scheduled task that launches the background
/// Host when the current user logs on (RUN-001). The task ACL is restricted to the current user SID
/// (SID ACL signal). This class is only loadable on Windows (net8.0-windows); it does not compile or
/// run in the Linux/WinUI-free sandbox — T08 defers its build/integration to a real Windows x64.
/// </summary>
public sealed class WindowsTaskSchedulerRegistrar : ITaskScheduler, IDisposable
{
    // Task Scheduler 2.0 COM constants.
    private const int TaskActionExec = 0;
    private const int TaskTriggerLogon = 9;
    private const int TaskLogonInteractiveToken = 3;
    private const int TaskRunLevelLua = 0;
    private const int TaskInstancesIgnoreNew = 1;
    private const int TaskCreateOrUpdate = 6;

    private dynamic? _taskService;

    private dynamic TaskService
    {
        get
        {
            if (_taskService is null)
            {
                var type = Type.GetTypeFromProgID("Schedule.Service")
                           ?? throw new PlatformNotSupportedException("Task Scheduler COM is unavailable on this platform.");
                _taskService = Activator.CreateInstance(type);
                _taskService!.Connect();
            }
            return _taskService;
        }
    }

    public Task<Result<HostTaskStatus>> RegisterAsync(HostTaskDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        try
        {
            var task = TaskService.NewTask(0);

            task.RegistrationInfo.Description = descriptor.Description;
            task.RegistrationInfo.Author = descriptor.UserId;
            if (!string.IsNullOrEmpty(descriptor.SecurityDescriptorSddl))
                task.RegistrationInfo.SecurityDescriptorSddl = descriptor.SecurityDescriptorSddl;

            // Run only for the current user, interactively, with least privilege (no elevation).
            task.Principal.UserId = descriptor.UserId;
            task.Principal.LogonType = TaskLogonInteractiveToken;
            task.Principal.RunLevel = TaskRunLevelLua;

            task.Settings.DisallowStartIfOnBatteries = false;
            task.Settings.StopIfGoingOnBatteries = false;
            task.Settings.MultipleInstances = TaskInstancesIgnoreNew;
            task.Settings.AllowHardTerminate = true;

            foreach (var trigger in descriptor.Triggers)
            {
                if (trigger.Kind != HostTriggerKind.Logon) continue;
                var logon = task.Triggers.Create(TaskTriggerLogon);
                logon.UserId = descriptor.UserId;
                logon.Enabled = true;
            }

            var action = task.Actions.Create(TaskActionExec);
            action.Path = descriptor.ExecutablePath;
            action.Arguments = descriptor.Arguments;

            dynamic root = TaskService.GetFolder("\\");
            root.RegisterTaskDefinition(
                descriptor.TaskName,
                task,
                TaskCreateOrUpdate,
                null,        // userId (use principal's)
                null,        // password
                TaskLogonInteractiveToken,
                null);       // sddl (already on RegistrationInfo)

            return Task.FromResult(Result<HostTaskStatus>.Ok(HostTaskStatus.Registered));
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return Task.FromResult(Result<HostTaskStatus>.Fail(SchedulerErrors.RegistrationError()));
        }
    }

    public Task<Result<HostTaskStatus>> QueryAsync(string taskName, CancellationToken cancellationToken = default)
    {
        try
        {
            dynamic root = TaskService.GetFolder("\\");
            dynamic task = root.GetTask(taskName); // throws if not found
            int state = (int)task.State; // 1=Disabled, 2=Queued, 3=Ready, 4=Running
            var status = state switch
            {
                1 => HostTaskStatus.Disabled,
                4 => HostTaskStatus.Running,
                _ => HostTaskStatus.Registered
            };
            return Task.FromResult(Result<HostTaskStatus>.Ok(status));
        }
        catch (FileNotFoundException)
        {
            return Task.FromResult(Result<HostTaskStatus>.Ok(HostTaskStatus.NotFound));
        }
        catch (COMException)
        {
            return Task.FromResult(Result<HostTaskStatus>.Fail(SchedulerErrors.QueryError()));
        }
    }

    public Task<Result<bool>> RemoveAsync(string taskName, CancellationToken cancellationToken = default)
    {
        try
        {
            dynamic root = TaskService.GetFolder("\\");
            root.DeleteTask(taskName, 0); // throws FileNotFoundException if absent -> treated as removed
            return Task.FromResult(Result<bool>.Ok(true));
        }
        catch (FileNotFoundException)
        {
            return Task.FromResult(Result<bool>.Ok(true)); // idempotent: already gone
        }
        catch (COMException)
        {
            return Task.FromResult(Result<bool>.Fail(SchedulerErrors.RemoveError()));
        }
    }

    public Task<Result<HostHealth>> GetHealthAsync(string taskName, CancellationToken cancellationToken = default)
    {
        try
        {
            dynamic root = TaskService.GetFolder("\\");
            dynamic task = root.GetTask(taskName);
            int lastResult = (int)task.LastTaskResult; // 0 = success
            var lastRun = (DateTime)task.LastRunTime;
            var last = lastRun == DateTime.MinValue ? (DateTimeOffset?)null : new DateTimeOffset(lastRun);
            var health = lastResult == 0 && last is not null
                ? HostHealth.Healthy(last.Value)
                : HostHealth.Degraded(last ?? DateTimeOffset.MinValue, $"LastTaskResult={lastResult}");
            return Task.FromResult(Result<HostHealth>.Ok(health));
        }
        catch (FileNotFoundException)
        {
            return Task.FromResult(Result<HostHealth>.Ok(HostHealth.Unknown("task not registered")));
        }
        catch (COMException)
        {
            return Task.FromResult(Result<HostHealth>.Fail(SchedulerErrors.HealthError()));
        }
    }

    public void Dispose()
    {
        if (_taskService is MarshalByRefObject mbr)
            Marshal.ReleaseComObject(mbr);
        _taskService = null;
    }
}
