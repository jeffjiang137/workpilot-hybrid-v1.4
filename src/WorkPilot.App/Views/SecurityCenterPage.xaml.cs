using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;
using WorkPilot.App.Core.Security;
using WorkPilot.Application.Security.Governance;
using WorkPilot.Domain.PermissionGovernance;
using WorkPilot.Domain.Security;
using WorkPilot.Domain.Security.Audit;

namespace WorkPilot.Views;

/// <summary>
/// Security Center page (SEC-101–107 / PER-008, T20). Hosts the six tabs (doc 06 §1) and binds to the
/// BCL <see cref="SecurityCenterViewModel"/> exposed by <c>App.Services.SecurityCenter</c>. All
/// governance logic lives in the view-model / Application services; this code-behind only builds view
/// inputs (resolved picker lists, selected incident for resolve) and routes button clicks. Action
/// buttons that need the templated item read it from the sender's DataContext to avoid x:Bind template
/// scope issues. WinUI compilation is gated to a real Windows build (doc 10 §16).
/// </summary>
public sealed partial class SecurityCenterPage : Page
{
    public SecurityCenterViewModel Vm { get; }

    public SecurityCenterPage()
    {
        Vm = App.Services.SecurityCenter;
        InitializeComponent();
        Loaded += async (_, _) => await Vm.LoadPostureAsync();
        PopulatePickers();
    }

    private void PopulatePickers()
    {
        ResolveCodeBox.ItemsSource = Enum.GetValues(typeof(IncidentResolutionCode)).Cast<IncidentResolutionCode>().ToList();
        ResolveCodeBox.SelectedIndex = 0;

        // First entry is null ("全部"); the rest are the AuditCategory values.
        var categories = new List<object?> { null };
        categories.AddRange(Enum.GetValues(typeof(AuditCategory)).Cast<object>());
        AuditCategoryBox.ItemsSource = categories;
        AuditCategoryBox.SelectedIndex = 0;
    }

    private void OnTabChanged(object sender, TabViewSelectionChangedEventArgs e)
    {
        if (e.NewIndex >= 0)
            Vm.SelectTab((SecurityCenterTab)e.NewIndex);
    }

    // ---- Incidents ----
    private async void OnIncidentSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView lv && lv.SelectedItem is Incident inc)
            await Vm.Incidents.OpenAsync(inc.Id);
    }

    private async void OnAckIncident(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is Incident inc)
            await Vm.Incidents.AcknowledgeAsync(inc.Id);
    }

    private async void OnMitigateIncident(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is Incident inc)
            await Vm.Incidents.MitigateAsync(inc.Id);
    }

    private async void OnAckSelectedIncident(object sender, RoutedEventArgs e)
    {
        if (Vm.Incidents.SelectedIncident is not null)
            await Vm.Incidents.AcknowledgeAsync(Vm.Incidents.SelectedIncident.Id);
    }

    private async void OnMitigateSelectedIncident(object sender, RoutedEventArgs e)
    {
        if (Vm.Incidents.SelectedIncident is not null)
            await Vm.Incidents.MitigateAsync(Vm.Incidents.SelectedIncident.Id);
    }

    private async void OnResolveIncident(object sender, RoutedEventArgs e)
    {
        if (Vm.Incidents.SelectedIncident is null) return;
        var code = (IncidentResolutionCode)(ResolveCodeBox.SelectedItem ?? IncidentResolutionCode.Remediated);
        var note = ResolveNoteBox.Text ?? string.Empty;
        await Vm.Incidents.ResolveAsync(Vm.Incidents.SelectedIncident.Id, code, note);
    }

    // ---- Sources ----
    private async void OnDisableSource(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is SourceHealth h)
            await Vm.Sources.DisableAsync(new SourceRef(h.Kind, h.Id));
    }

    private async void OnRecoverSource(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is SourceHealth h)
            await Vm.Sources.RecoverAsync(new SourceRef(h.Kind, h.Id));
    }

    // ---- Grants ----
    private async void OnPreviewGrant(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is PolicyGrant g)
            await Vm.Grants.PreviewRevokeAsync(g.GrantId);
    }

    private async void OnRevokeGrant(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is PolicyGrant g)
            await Vm.Grants.RevokeAsync(g.GrantId);
    }

    // ---- Audit ----
    private void OnAuditCategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        Vm.Audit.CategoryFilter = AuditCategoryBox.SelectedItem is AuditCategory c ? c : null;
    }

    private void OnAuditEntrySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView lv && lv.SelectedItem is AuditEntry entry)
            Vm.Audit.SelectEntry(entry);
    }

    // ---- Operations / support package ----
    private async void OnPickOutputPath(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainAppWindow));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null) Vm.Support.OutputPath = folder.Path;
        }
        catch (Exception ex)
        {
            // Path picker is best-effort in source-delivery; surface nothing destructive.
            System.Diagnostics.Debug.WriteLine(ex.Message);
        }
    }
}
