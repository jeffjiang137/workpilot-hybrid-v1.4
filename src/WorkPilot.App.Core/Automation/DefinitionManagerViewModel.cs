using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using WorkPilot.Application.Automation.Definition;
using WorkPilot.Application.Automation.Run;
using WorkPilot.App.Core.Primitives;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.PermissionGovernance.Evaluation;

namespace WorkPilot.App.Core.Automation;

/// <summary>
/// BCL view model for the automation definition lifecycle (T22): export / import / dry-run /
/// enable preflight. UI-agnostic (no WinUI, no Repository/Connector/MCP/Secret/Native per AI
/// dev rule §3); it depends only on <see cref="IDefinitionManager"/>. Commands surface the last
/// result as an observable, non-secret read model and a transient <see cref="ErrorMessage"/>.
/// </summary>
public sealed class DefinitionManagerViewModel : ObservableBase
{
    private readonly IDefinitionManager _manager;

    public DefinitionManagerViewModel(IDefinitionManager manager)
        => _manager = manager ?? throw new ArgumentNullException(nameof(manager));

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

    private string? _errorMessage;
    public string? ErrorMessage { get => _errorMessage; private set => Set(ref _errorMessage, value); }

    private DefinitionExport? _lastExport;
    public DefinitionExport? LastExport { get => _lastExport; private set => Set(ref _lastExport, value); }

    private ImportedAutomation? _lastImport;
    public ImportedAutomation? LastImport { get => _lastImport; private set => Set(ref _lastImport, value); }

    private DryRunPlan? _lastDryRun;
    public DryRunPlan? LastDryRun { get => _lastDryRun; private set => Set(ref _lastDryRun, value); }

    private PreflightResult? _lastPreflight;
    public PreflightResult? LastPreflight { get => _lastPreflight; private set => Set(ref _lastPreflight, value); }

    /// <summary>The point-in-time policy context for the preflight. The host/UI supplies the live
    /// source / space / grant / epoch / emergency / clock state before invoking <see cref="PreflightCommand"/>.</summary>
    public EvaluationContext? PreflightContext { get; set; }

    public ICommand ExportCommand => new AsyncRelayCommand((p, _) => ExportAsync(p));
    public ICommand ImportCommand => new AsyncRelayCommand((p, _) => ImportAsync(p));
    public ICommand DryRunCommand => new AsyncRelayCommand((p, _) => DryRunAsync(p));
    public ICommand PreflightCommand => new AsyncRelayCommand((p, _) => PreflightAsync(p));

    private async Task ExportAsync(object? param)
    {
        if (!TryId(param, out var id)) { ErrorMessage = "无效的自动化 ID"; return; }
        await RunAsync(_manager.ExportAsync(id), r => LastExport = r);
    }

    private async Task ImportAsync(object? param)
    {
        if (param is not string json || string.IsNullOrWhiteSpace(json)) { ErrorMessage = "导入内容为空"; return; }
        await RunAsync(_manager.ImportAsync(json), r => LastImport = r);
    }

    private async Task DryRunAsync(object? param)
    {
        if (!TryId(param, out var id)) { ErrorMessage = "无效的自动化 ID"; return; }
        IsBusy = true; ErrorMessage = null;
        try
        {
            LastDryRun = await _manager.DryRunAsync(id, CancellationToken.None);
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        finally { IsBusy = false; }
    }

    private async Task PreflightAsync(object? param)
    {
        if (!TryId(param, out var id)) { ErrorMessage = "无效的自动化 ID"; return; }
        var ctx = PreflightContext;
        if (ctx is null) { ErrorMessage = "预检上下文缺失"; return; }
        IsBusy = true; ErrorMessage = null;
        try
        {
            LastPreflight = await _manager.PreflightAsync(id, ctx, CancellationToken.None);
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        finally { IsBusy = false; }
    }

    private async Task RunAsync<T>(Task<Result<T>> work, Action<T> onSuccess)
    {
        IsBusy = true; ErrorMessage = null;
        try
        {
            var r = await work.ConfigureAwait(false);
            if (r.IsSuccess) onSuccess(r.Value!);
            else ErrorMessage = r.Error!.MessageKey;
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        finally { IsBusy = false; }
    }

    private static bool TryId(object? param, out AutomationId id)
        => AutomationId.TryParse(param as string, out id);
}
