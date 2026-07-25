using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WorkPilot.App.Core.Permissions;
using WorkPilot.Application.Permission.Policy;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.PermissionGovernance;
using WorkPilot.Domain.PermissionGovernance.Evaluation;

namespace WorkPilot.Views;

/// <summary>
/// Permission page (PER-003/004/008–010, T18). Binds to the BCL <see cref="PolicyPermissionsViewModel"/>
/// constructed by the composition root (<c>App.Services.Permissions</c>). All governance logic lives in
/// the view-model / Application services; this code-behind only builds view inputs (projection context,
/// edited statements, impact targets) and surfaces errors. WinUI compilation is gated to a real Windows
/// build (doc 10 §16).
/// </summary>
public sealed partial class PermissionsPage : Page
{
    public PolicyPermissionsViewModel Vm { get; }

    public PermissionsPage()
    {
        Vm = App.Services.Permissions;
        InitializeComponent();
        PopulatePickers();
        Loaded += async (_, _) => await SafeAsync(() => Vm.LoadGrantsAsync());
    }

    private void PopulatePickers()
    {
        LayerBox.ItemsSource = new[] { PolicyLayer.SpacePolicy, PolicyLayer.ExpertPolicy, PolicyLayer.AutomationPolicy };
        LayerBox.SelectedIndex = 0;
        EffectBox.ItemsSource = new[] { PolicyEffect.Allow, PolicyEffect.Ask, PolicyEffect.Deny };
        EffectBox.SelectedIndex = 0;
        RiskBox.ItemsSource = new[] { RiskLevel.Low, RiskLevel.Medium, RiskLevel.High, RiskLevel.Critical };
        RiskBox.SelectedIndex = 1;
    }

    private async void OnProject(object sender, RoutedEventArgs e)
    {
        var schema = string.IsNullOrWhiteSpace(SourceSchemaBox.Text) ? string.Empty : SourceSchemaBox.Text.Trim();
        var spaceId = App.Services.ActiveSpace?.Id;
        var ctx = new EvaluationContext(
            PolicySubject.AutomationPrincipal,
            spaceId ?? "src-1",
            schema,
            sourceEnabled: true,
            sourceQuarantined: false,
            spaceLinked: spaceId is not null,
            spaceId,
            expertGranted: false,
            emergencyStopActive: false,
            currentEpoch: 0,
            automationGrantPresent: false,
            DateTimeOffset.UtcNow,
            "interactive", "manual", 1, 0, "healthy");

        var queries = CapabilityIdsBox.Text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(id => new CapabilityQuery(id, schema, RiskLevel.Medium, null))
            .ToList();
        if (queries.Count == 0)
        {
            ShowError("请先输入至少一个能力 StableId。");
            return;
        }

        Vm.SetProjectionContext(ctx, queries);
        await SafeAsync(() => Vm.ProjectAsync(ctx, queries));
    }

    private async void OnRefreshGrants(object sender, RoutedEventArgs e)
        => await SafeAsync(() => Vm.LoadGrantsAsync());

    private async void OnPreview(object sender, RoutedEventArgs e)
    {
        if (EditCapabilityBox.Text.Trim() is not { Length: > 0 } capId)
        {
            ShowError("请先输入要编辑的能力 StableId。");
            return;
        }
        var layer = (PolicyLayer)LayerBox.SelectedItem!;
        var effect = (PolicyEffect)EffectBox.SelectedItem!;
        var risk = (RiskLevel)RiskBox.SelectedItem!;
        var spaceId = App.Services.ActiveSpace?.Id;
        var sourceStableId = spaceId ?? "src-1";

        var ids = new DeterministicIdGenerator("preview");
        var versionId = PolicyVersionId.Create(ids);
        var stmt = PolicyStatement.Create(
            ids, versionId,
            enabled: true,
            effect,
            new[] { PolicySubject.AutomationPrincipal },
            /* sourceSelectorJson */ $"{{\"source\":\"mcp:{sourceStableId}\"}}",
            /* capabilitySelectorJson */ $"{{\"capability\":\"{capId}\"}}",
            RiskLevel.Low, risk,
            scope: null,
            Array.Empty<PolicyCondition>(),
            priority: 0);

        var target = new ImpactTarget(
            automationId: "preview-auto",
            sourceKind: "mcp",
            sourceStableId,
            sourceSchemaSha256: string.Empty,
            capabilityStableId: capId,
            capabilitySchemaSha256: string.Empty,
            argumentRisk: risk,
            invocationScope: null,
            spaceId);

        var report = await SafeAsync(() => Vm.PrepareSaveAsync(layer, spaceId, new[] { stmt }, new[] { target }, "user"));
        if (report is null)
            ShowError(Vm.LastError ?? "预览失败。");
    }

    private async Task SafeAsync(Func<Task> work)
    {
        try { await work(); if (!string.IsNullOrEmpty(Vm.LastError)) ShowError(Vm.LastError!); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void ShowError(string message)
    {
        InfoBar.Message = message;
        InfoBar.Severity = InfoBarSeverity.Error;
        InfoBar.IsOpen = true;
    }
}
