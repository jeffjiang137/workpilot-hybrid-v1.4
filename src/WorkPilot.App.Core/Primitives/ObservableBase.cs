using System.Collections.Generic;
using System.ComponentModel;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;

namespace WorkPilot.App.Core.Primitives;

/// <summary>
/// Minimal <see cref="INotifyPropertyChanged"/> base for editor view models. BCL-only (no WinUI
/// dependency) so the same type binds to WinUI XAML and is unit-testable on any platform (AI dev
/// rule §3: ViewModel does not access Repository/Connector/MCP/Secret/Native).
/// </summary>
public abstract class ObservableBase : INotifyPropertyChanged
{
    /// <summary>Raised on the thread that mutated the property (UI must marshal to the dispatcher).</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Sets <paramref name="field"/> and raises <see cref="PropertyChanged"/> only when the value
    /// actually changes. Returns true when a change was published.
    /// </summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        Raise(propertyName);
        return true;
    }

    /// <summary>Raises <see cref="PropertyChanged"/> for the named property (and optionally dependent properties).</summary>
    protected void Raise([CallerMemberName] string? propertyName = null)
    {
        if (propertyName is null)
            return;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>Raises <see cref="PropertyChanged"/> for a computed property referenced by a lambda (kept for readability).</summary>
    protected void Raise<T>(Expression<Func<T>> propertyExpression)
    {
        if (propertyExpression.Body is MemberExpression member)
            Raise(member.Member.Name);
    }
}
