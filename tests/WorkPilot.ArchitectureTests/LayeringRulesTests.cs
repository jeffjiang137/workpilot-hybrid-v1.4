using System.Collections.Generic;
using System.Reflection;
using NetArchTest.Rules;
using Xunit;

namespace WorkPilot.ArchitectureTests;

/// <summary>
/// T01 architecture-boundary guardrails. These tests encode the layering contract
/// (Contracts / Domain / Application / Infrastructure / Host) and must pass for T01's DoD.
/// They run on Windows (net8.0-windows) so they can load the Windows-targeted assemblies.
/// </summary>
public sealed class LayeringRulesTests
{
    private static readonly Assembly Contracts = typeof(WorkPilot.Contracts.Composition.ICompositionRoot).Assembly;
    private static readonly Assembly Domain = typeof(WorkPilot.Domain.LayerMarker).Assembly;
    private static readonly Assembly Application = typeof(WorkPilot.Application.Composition.CompositionRoot).Assembly;
    private static readonly Assembly Infrastructure = typeof(WorkPilot.Infrastructure.LayerMarker).Assembly;
    private static readonly Assembly Host = typeof(WorkPilot.Host.LayerMarker).Assembly;
    private static readonly Assembly HostCore = typeof(WorkPilot.Host.Core.Scheduling.HostTaskName).Assembly;
    private static readonly Assembly AppCore = typeof(WorkPilot.App.Core.Automation.AutomationEditorSession).Assembly;

    [Fact]
    public void Domain_must_not_depend_on_Application_Infrastructure_or_Host()
    {
        var violations = Violations(Domain, "WorkPilot.Application", "WorkPilot.Infrastructure", "WorkPilot.Host");
        Assert.True(string.IsNullOrEmpty(violations), "Domain must not depend on Application, Infrastructure, or Host. Violations: " + violations);
    }

    [Fact]
    public void Application_must_not_depend_on_Infrastructure_or_Host()
    {
        var violations = Violations(Application, "WorkPilot.Infrastructure", "WorkPilot.Host");
        Assert.True(string.IsNullOrEmpty(violations), "Application must not depend on Infrastructure or Host. Violations: " + violations);
    }

    [Fact]
    public void Infrastructure_must_not_depend_on_Host_or_App()
    {
        var violations = Violations(Infrastructure, "WorkPilot.Host", "WorkPilot.App");
        Assert.True(string.IsNullOrEmpty(violations), "Infrastructure must not depend on Host or the WinUI App. Violations: " + violations);
    }

    [Fact]
    public void Host_must_not_reference_WinUI_or_the_WinUI_App()
    {
        // Same WinUI-namespace note as AppCore/HostCore: "WorkPilot.App" prefix-matches
        // "WorkPilot.Application"; target the WinUI-only namespaces instead.
        var violations = Violations(Host, "Microsoft.UI.Xaml",
            "WorkPilot.Services", "WorkPilot.Views", "WorkPilot.Models");
        Assert.True(string.IsNullOrEmpty(violations), "Host must not reference WinUI or the WinUI App assembly. Violations: " + violations);
    }

    [Fact]
    public void AppCore_must_not_depend_on_Infrastructure_Host_or_WinUI_App()
    {
        // NOTE: the WinUI App project (WorkPilot.App) exposes its types under WorkPilot.Services /
        // WorkPilot.Views / WorkPilot.Models (its root namespace is WorkPilot, not "WorkPilot.App").
        // The literal token "WorkPilot.App" is intentionally NOT used here: NetArchTest prefix-matches
        // it against "WorkPilot.Application" (the legitimate app layer App.Core depends on), which would
        // be a false positive. We target the WinUI-only namespaces instead.
        var violations = Violations(AppCore, "WorkPilot.Infrastructure", "WorkPilot.Host", "Microsoft.UI.Xaml",
            "WorkPilot.Services", "WorkPilot.Views", "WorkPilot.Models");
        Assert.True(string.IsNullOrEmpty(violations), "App.Core must not depend on Infrastructure, Host, WinUI, or the WinUI App. Violations: " + violations);
    }

    [Fact]
    public void HostCore_must_not_depend_on_Host_App_Infrastructure_or_WinUI()
    {
        // The Win32 Host layer lives under WorkPilot.Host.Hosting / WorkPilot.Host.Scheduling (its root
        // namespace is "WorkPilot.Host", which would prefix-match this assembly's own "WorkPilot.Host.Core"
        // and cause a false positive). Target the Win32 sub-namespaces precisely.
        var violations = Violations(HostCore, "WorkPilot.Host.Hosting", "WorkPilot.Host.Scheduling",
            "WorkPilot.Infrastructure", "Microsoft.UI.Xaml",
            "WorkPilot.Services", "WorkPilot.Views", "WorkPilot.Models");
        Assert.True(string.IsNullOrEmpty(violations), "Host.Core must not depend on Host, the WinUI App, Infrastructure, or WinUI. Violations: " + violations);
    }

    [Fact]
    public void Contracts_must_have_no_WorkPilot_layer_dependencies()
    {
        var violations = Violations(Contracts, "WorkPilot.Domain", "WorkPilot.Application", "WorkPilot.Infrastructure", "WorkPilot.Host");
        Assert.True(string.IsNullOrEmpty(violations), "Contracts must not depend on any other WorkPilot layer. Violations: " + violations);
    }

    private static string Violations(Assembly assembly, params string[] namespaces)
    {
        var failures = new List<string>();
        foreach (var ns in namespaces)
        {
            var result = Types.InAssembly(assembly).Should().NotHaveDependencyOn(ns).GetResult();
            if (!result.IsSuccessful && result.FailingTypeNames is not null)
            {
                failures.AddRange(result.FailingTypeNames);
            }
        }

        return string.Join("; ", failures);
    }
}
