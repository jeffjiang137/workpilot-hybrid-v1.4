using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WorkPilot.App.Core.Automation;
using WorkPilot.Domain.PermissionGovernance.Evaluation;

namespace WorkPilot.Views;

/// <summary>
/// WinUI host page for the automation definition lifecycle (T22): export / import / dry-run /
/// enable preflight. Binds to <see cref="DefinitionManagerViewModel"/> (BCL). The host builds the
/// view model via <c>DefinitionServices.BuildManager(...)</c>, assigns <see cref="Vm"/>, and supplies
/// <see cref="PreflightContextFactory"/> with the LIVE policy context (source / space / grant /
/// epoch / emergency / clock) used by the preflight.
/// </summary>
public sealed partial class DefinitionManagerPage : Page
{
    public DefinitionManagerViewModel? Vm
    {
        get => DataContext as DefinitionManagerViewModel;
        set => DataContext = value;
    }

    /// <summary>The host supplies the live <see cref="EvaluationContext"/> for the preflight so it
    /// reflects run-time state. Set before the page is shown.</summary>
    public Func<EvaluationContext>? PreflightContextFactory { get; set; }

    public DefinitionManagerPage() => InitializeComponent();

    private void PreflightButton_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null || PreflightContextFactory is null)
            return; // Vm surfaces "预检上下文缺失" when context is null
        Vm.PreflightContext = PreflightContextFactory();
        Vm.PreflightCommand.Execute(IdTextBox.Text);
    }
}
