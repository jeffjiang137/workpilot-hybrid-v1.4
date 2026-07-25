namespace WorkPilot.Contracts.Composition;

/// <summary>
/// Builds the application's composed service provider from the layered architecture.
/// Implemented in the Application layer; consumed by hosts (the WinUI App and the
/// background Host process). This is the single composition-root contract shared
/// across layers so that no upper layer depends on a concrete composition implementation.
/// </summary>
public interface ICompositionRoot
{
    IServiceProvider Build();
}
