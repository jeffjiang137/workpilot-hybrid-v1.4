using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Application.Security.Retention;
using WorkPilot.App.Core.Primitives;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;

namespace WorkPilot.App.Core.Security;

/// <summary>
/// Operations tab — export a single run as a privacy-safe report (LOG-005). The report intentionally
/// contains no prompt, parameters or results (those live in the run snapshot and are never serialized).
/// The view model only drives the BCL facade; the actual projection happens in
/// <see cref="IRunReportExporter"/> behind <see cref="ISecurityCenterDataProvider"/>.
/// </summary>
public sealed class RunReportExportViewModel : ObservableBase
{
    private readonly ISecurityCenterDataProvider _provider;
    private string? _runIdText;
    private string? _savePath;
    private bool _isBusy;
    private AppError? _error;
    private RunReport? _report;

    public RunReportExportViewModel(ISecurityCenterDataProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        ExportCommand = new AsyncRelayCommand((_, ct) => ExportAsync(ct), _ => !_isBusy);
        SaveAsCommand = new AsyncRelayCommand((_, ct) => SaveAsAsync(ct), _ => !_isBusy && _report is not null && !string.IsNullOrWhiteSpace(_savePath));
    }

    public string? RunIdText { get => _runIdText; set => Set(ref _runIdText, value); }
    public string? SavePath { get => _savePath; set => Set(ref _savePath, value); }

    public RunReport? Report { get => _report; private set => Set(ref _report, value); }
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
    public AppError? Error { get => _error; private set { if (Set(ref _error, value)) Raise(nameof(HasError)); } }
    public bool HasError => _error is not null;

    public AsyncRelayCommand ExportCommand { get; }
    public AsyncRelayCommand SaveAsCommand { get; }

    public async Task<bool> ExportAsync(CancellationToken ct = default)
    {
        IsBusy = true; Error = null; Report = null;
        try
        {
            if (!RunId.TryParse(_runIdText, out var id))
            {
                Error = new AppError("SEC_RPT_INVALID_ID", ErrorCategory.Validation,
                    "SecurityCenter.RunReportInvalidId", false);
                return false;
            }

            var res = await _provider.ExportRunReportAsync(id, ct).ConfigureAwait(false);
            if (!res.IsSuccess) { Error = res.Error!; return false; }
            Report = res.Value!;
            return true;
        }
        finally { IsBusy = false; }
    }

    public async Task<bool> SaveAsAsync(CancellationToken ct = default)
    {
        if (_report is null || string.IsNullOrWhiteSpace(_savePath))
        {
            Error = new AppError("SEC_RPT_NO_TARGET", ErrorCategory.Validation,
                "SecurityCenter.RunReportNoTarget", false);
            return false;
        }

        IsBusy = true; Error = null;
        try
        {
            var json = JsonSerializer.Serialize(_report, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_savePath, json, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Error = new AppError("SEC_RPT_WRITE_FAILED", ErrorCategory.Internal,
                "SecurityCenter.RunReportWriteFailed", false,
                new Dictionary<string, string> { ["detail"] = ex.Message });
            return false;
        }
        finally { IsBusy = false; }
    }
}
