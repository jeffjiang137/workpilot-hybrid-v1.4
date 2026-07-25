using WorkPilot.Contracts.Primitives;

namespace WorkPilot.Domain.Automation;

/// <summary>Raised by invariant validators; carries the mapped <see cref="AppError"/>.</summary>
public sealed class DomainException : Exception
{
    public AppError Error { get; }

    public DomainException(AppError error) : base(error.MessageKey) => Error = error;
}
