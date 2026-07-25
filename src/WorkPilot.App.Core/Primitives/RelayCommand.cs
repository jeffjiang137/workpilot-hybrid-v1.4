using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace WorkPilot.App.Core.Primitives;

/// <summary>
/// Synchronous <see cref="ICommand"/> with a re-entrancy-safe <see cref="CanExecute"/> guard.
/// The service layer still validates; the guard only prevents double-clicks (AI dev rule §10).
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private int _executing;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) =>
        (_canExecute?.Invoke(parameter) ?? true) && Interlocked.CompareExchange(ref _executing, 1, 1) == 0;

    public void Execute(object? parameter)
    {
        // Re-entrancy guard: a second invocation while one is in flight is ignored, not queued.
        if (Interlocked.Exchange(ref _executing, 1) != 0)
            return;
        try
        {
            _execute(parameter);
        }
        finally
        {
            Interlocked.Exchange(ref _executing, 0);
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Asynchronous <see cref="ICommand"/> with busy-state tracking and re-entrancy guard. The async
/// work runs via <see cref="Task.Run"/> only when the caller supplies a delegate; XAML handlers
/// delegate to this command (AI dev rule §59: only XAML handlers may be async void).
/// </summary>
public sealed class AsyncRelayCommand : ICommand, IDisposable
{
    private readonly Func<object?, CancellationToken, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private readonly CancellationTokenSource _linkedSource = new();
    private int _executing;

    public AsyncRelayCommand(Func<object?, CancellationToken, Task> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;
    public event EventHandler? ExecutionCompleted;

    /// <summary>True while an invocation is in flight. Bind busy indicators to this.</summary>
    public bool IsExecuting => Volatile.Read(ref _executing) == 1;

    public bool CanExecute(object? parameter) =>
        (_canExecute?.Invoke(parameter) ?? true) && !IsExecuting;

    public async void Execute(object? parameter)
    {
        if (Interlocked.Exchange(ref _executing, 1) != 0)
            return; // re-entrancy guard
        RaiseCanExecute();
        try
        {
            await _execute(parameter, _linkedSource.Token).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _executing, 0);
            RaiseCanExecute();
            ExecutionCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Cancels the in-flight operation if the command supports cooperative cancellation.</summary>
    public void Cancel() => _linkedSource.Cancel();

    private void RaiseCanExecute() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose() => _linkedSource.Dispose();
}
