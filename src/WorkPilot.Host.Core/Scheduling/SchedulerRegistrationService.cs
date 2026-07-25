using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Contracts.Primitives;

using WorkPilot.Host.Core.Health;

namespace WorkPilot.Host.Core.Scheduling;

/// <summary>Outcome of a registration attempt, distinguishing first-create from idempotent re-calls.</summary>
public enum RegistrationOutcome
{
    Created,
    AlreadyRegistered,
    Updated,
}

/// <summary>Result of a registration attempt.</summary>
public sealed record RegistrationResult(RegistrationOutcome Outcome, HostTaskStatus Status);

/// <summary>
/// Pure orchestration of background-Host task registration (T08). It resolves the current user SID,
/// anti-tamper-validates the Host executable path, builds the <see cref="HostTaskDescriptor"/>, and
/// decides create / update / no-op against the existing OS task — all without touching COM, so it is
/// unit-testable with a stub <see cref="ITaskScheduler"/> on any platform.
///
/// The Windows COM implementation of <see cref="ITaskScheduler"/> lives in <c>WorkPilot.Host</c>.
/// </summary>
public sealed class SchedulerRegistrationService
{
    private readonly ITaskScheduler _scheduler;
    private readonly ISidResolver _sidResolver;
    private readonly ExecutablePathValidator _pathValidator;
    private readonly string _appId;
    private readonly string _installRoot;
    private readonly string _arguments;

    public SchedulerRegistrationService(
        ITaskScheduler scheduler,
        ISidResolver sidResolver,
        ExecutablePathValidator pathValidator,
        string appId,
        string installRoot,
        string arguments = "")
    {
        _scheduler = scheduler ?? throw new System.ArgumentNullException(nameof(scheduler));
        _sidResolver = sidResolver ?? throw new System.ArgumentNullException(nameof(sidResolver));
        _pathValidator = pathValidator ?? throw new System.ArgumentNullException(nameof(pathValidator));
        _appId = appId;
        _installRoot = installRoot;
        _arguments = arguments;
    }

    public string TaskName => HostTaskName.ForApp(_appId);

    /// <summary>Build the descriptor that the OS scheduler will materialize.</summary>
    public async Task<Result<HostTaskDescriptor>> BuildDescriptorAsync(
        string expectedExecutablePath,
        CancellationToken cancellationToken = default)
    {
        var sidResult = await ResolveSidAsync(cancellationToken);
        if (!sidResult.IsSuccess)
            return Result<HostTaskDescriptor>.Fail(sidResult.Error!);

        var pathCheck = _pathValidator.Validate(expectedExecutablePath, _installRoot);
        if (!pathCheck.IsSuccess)
            return Result<HostTaskDescriptor>.Fail(pathCheck.Error!);

        var descriptor = new HostTaskDescriptor(
            TaskName,
            expectedExecutablePath,
            _arguments,
            sidResult.Value!,
            HostLogonType.InteractiveToken,
            new[] { sidResult.Value! },
            new[] { new SchedulerTrigger(HostTriggerKind.Logon, null) },
            "WorkPilot background host (RUN-001): runs automations when the main app is closed.");

        return Result<HostTaskDescriptor>.Ok(descriptor);
    }

    /// <summary>
    /// Register (or idempotently re-confirm) the Host task. Repeated calls for an already-registered
    /// task are a no-op (outcome <see cref="RegistrationOutcome.AlreadyRegistered"/>) and do not call
    /// the OS registrar again, so duplicate registration is impossible.
    /// </summary>
    public async Task<Result<RegistrationResult>> RegisterAsync(
        string expectedExecutablePath,
        CancellationToken cancellationToken = default)
    {
        var descriptor = await BuildDescriptorAsync(expectedExecutablePath, cancellationToken);
        if (!descriptor.IsSuccess)
            return Result<RegistrationResult>.Fail(descriptor.Error!);

        var existing = await _scheduler.QueryAsync(TaskName, cancellationToken);
        if (!existing.IsSuccess)
            return Result<RegistrationResult>.Fail(SchedulerErrors.QueryError());

        if (existing.Value == HostTaskStatus.Registered || existing.Value == HostTaskStatus.Running)
            return Result<RegistrationResult>.Ok(new RegistrationResult(RegistrationOutcome.AlreadyRegistered, existing.Value));

        var registered = await _scheduler.RegisterAsync(descriptor.Value!, cancellationToken);
        if (!registered.IsSuccess)
            return Result<RegistrationResult>.Fail(SchedulerErrors.RegistrationError());

        var outcome = existing.Value == HostTaskStatus.Disabled
            ? RegistrationOutcome.Updated
            : RegistrationOutcome.Created;
        return Result<RegistrationResult>.Ok(new RegistrationResult(outcome, registered.Value));
    }

    /// <summary>Remove the Host task if present (idempotent).</summary>
    public async Task<Result<bool>> RemoveAsync(CancellationToken cancellationToken = default)
    {
        var removed = await _scheduler.RemoveAsync(TaskName, cancellationToken);
        return removed.IsSuccess ? removed : Result<bool>.Fail(SchedulerErrors.RemoveError());
    }

    public async Task<Result<HostHealth>> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        var health = await _scheduler.GetHealthAsync(TaskName, cancellationToken);
        return health.IsSuccess ? health : Result<HostHealth>.Fail(SchedulerErrors.HealthError());
    }

    private async Task<Result<string>> ResolveSidAsync(CancellationToken cancellationToken)
    {
        try
        {
            var sid = await _sidResolver.ResolveCurrentUserSidAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(sid))
                return Result<string>.Fail(SchedulerErrors.SidResolutionError());
            return Result<string>.Ok(sid);
        }
        catch
        {
            return Result<string>.Fail(SchedulerErrors.SidResolutionError());
        }
    }
}
