using Microsoft.Extensions.DependencyInjection;
using WorkPilot.Contracts.Composition;

namespace WorkPilot.Application.Composition;

/// <summary>
/// Composition root for the layered architecture. In T01 this wires only the layering
/// contract (ICompositionRoot) so the project boundary and DI entry point exist.
/// Existing WorkPilot.App services are adapted into this root in later tasks; no runtime
/// behavior changes in T01.
/// </summary>
public sealed class CompositionRoot : ICompositionRoot
{
    public IServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICompositionRoot>(this);
        return services.BuildServiceProvider();
    }
}
