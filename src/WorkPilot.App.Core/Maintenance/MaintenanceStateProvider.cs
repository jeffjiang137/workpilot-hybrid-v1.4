using System;
using WorkPilot.Domain.Schema;

namespace WorkPilot.App.Core.Maintenance;

/// <summary>Host-provided channel that publishes the current maintenance posture to the UI (T23).</summary>
public interface IMaintenanceStateProvider
{
    MaintenanceState Current { get; }
    event EventHandler<MaintenanceState>? Changed;
    void SetState(MaintenanceState state);
}

/// <summary>Default in-process implementation of <see cref="IMaintenanceStateProvider"/> (BCL).</summary>
public sealed class MaintenanceStateProvider : IMaintenanceStateProvider
{
    private readonly object _gate = new();
    private MaintenanceState _current = MaintenanceState.None;

    public MaintenanceState Current
    {
        get { lock (_gate) return _current; }
    }

    public event EventHandler<MaintenanceState>? Changed;

    public void SetState(MaintenanceState state)
    {
        MaintenanceState previous;
        lock (_gate)
        {
            previous = _current;
            if (previous == state) return;
            _current = state;
        }

        Changed?.Invoke(this, state);
    }
}
