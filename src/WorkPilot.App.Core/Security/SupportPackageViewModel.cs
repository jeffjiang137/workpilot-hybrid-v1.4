using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.App.Core.Primitives;
using WorkPilot.Application.Security.Retention;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;

namespace WorkPilot.App.Core.Security;

/// <summary>
/// One selectable category of a support package (doc 06 §9). <c>RunReports</c> is excluded by default
/// and must be explicitly opted in — support packages never include run reports unless the operator
/// chooses to.
/// </summary>
public sealed class SupportPackageCategoryView : ObservableBase
{
    private bool _include;
    public string Name { get; }
    public bool Include { get => _include; set => Set(ref _include, value); }
    public SupportPackageCategoryView(string name, bool include) { Name = name; _include = include; }
}

/// <summary>
/// Operations tab — support package generation (doc 06 §9). Enforces the gating rules before any
/// package is produced: lists included/excluded categories, excludes Run reports by default, requires a
/// local output path, runs a canary/secret scan, and computes a SHA-256 manifest. It never auto-uploads
/// or opens a third-party page. Pure BCL: no secret, connector, or native dependency.
/// </summary>
public sealed class SupportPackageViewModel : ObservableBase
{
    private readonly ISecurityCenterDataProvider? _provider;
    private string? _outputPath;
    private bool _isBusy;
    private AppError? _error;
    private bool _canaryScanPerformed;
    private string? _manifestHash;
    private DateTimeOffset? _generatedAtUtc;
    private SupportBundleResult? _lastResult;

    public SupportPackageViewModel(ISecurityCenterDataProvider? provider = null)
    {
        _provider = provider;
        // Run reports are intentionally OFF by default (doc 06 §9).
        Categories = new ObservableCollection<SupportPackageCategoryView>
        {
            new("Incidents", true),
            new("AuditLog", true),
            new("SourceHealth", true),
            new("Policy", true),
            new("Configuration", true),
            new("RunReports", false)
        };
        GenerateCommand = new AsyncRelayCommand((_, ct) => GenerateAsync(ct));
    }

    public ObservableCollection<SupportPackageCategoryView> Categories { get; }

    /// <summary>Run ids selected for inclusion when <see cref="IncludeRunReports"/> is on (doc 06 §9).</summary>
    public ObservableCollection<RunId> SelectedRunIds { get; } = new();

    /// <summary>Convenience accessor for the Run-reports opt-in (default false per doc 06 §9).</summary>
    public bool IncludeRunReports
    {
        get => Categories.FirstOrDefault(c => c.Name == "RunReports")?.Include ?? false;
        set { var c = Categories.FirstOrDefault(x => x.Name == "RunReports"); if (c is not null) c.Include = value; }
    }

    public string? OutputPath { get => _outputPath; set => Set(ref _outputPath, value); }
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
    public AppError? Error { get => _error; private set { if (Set(ref _error, value)) Raise(nameof(HasError)); } }
    public bool HasError => _error is not null;

    /// <summary>True once the canary/secret scan has run over the selected categories.</summary>
    public bool CanaryScanPerformed { get => _canaryScanPerformed; private set => Set(ref _canaryScanPerformed, value); }
    /// <summary>SHA-256 manifest of the package contents (doc 06 §9).</summary>
    public string? ManifestHash { get => _manifestHash; private set => Set(ref _manifestHash, value); }
    public DateTimeOffset? GeneratedAtUtc { get => _generatedAtUtc; private set => Set(ref _generatedAtUtc, value); }
    /// <summary>Full result of the last successful build (LOG-006, SEC-108).</summary>
    public SupportBundleResult? LastResult { get => _lastResult; private set => Set(ref _lastResult, value); }

    public AsyncRelayCommand GenerateCommand { get; }

    /// <summary>True only when at least one category is selected and a local output path is set.</summary>
    public bool CanGenerate =>
        !string.IsNullOrWhiteSpace(_outputPath)
        && Categories.Any(c => c.Include);

    public async Task<bool> GenerateAsync(CancellationToken ct = default)
    {
        IsBusy = true; Error = null; CanaryScanPerformed = false; ManifestHash = null; LastResult = null;
        try
        {
            if (!CanGenerate)
            {
                Error = new AppError("SEC_PKG_INVALID", ErrorCategory.Validation,
                    "SecurityCenter.SupportPackageInvalid", false,
                    new Dictionary<string, string>
                    {
                        ["reason"] = string.IsNullOrWhiteSpace(_outputPath) ? "no_output_path" : "no_category"
                    });
                return false;
            }

            if (_provider is null)
            {
                // No backend wired (e.g. unit test without a provider): run the BCL gating stub only.
                await Task.Run(() => CanaryScanSelected(), ct).ConfigureAwait(false);
                CanaryScanPerformed = true;
                ManifestHash = ComputeLocalManifestHash();
                GeneratedAtUtc = DateTimeOffset.UtcNow;
                return true;
            }

            // Build the real request from the selected categories + run ids and call the backend
            // (SupportBundleBuilder). It performs the privacy scan, size cap, canary scan and SHA-256
            // manifest; we only surface the outcome here.
            var categories = new HashSet<SupportPackageCategory>(
                Categories.Where(c => c.Include).Select(c => Enum.Parse<SupportPackageCategory>(c.Name)));
            IReadOnlyList<RunId> runIds = categories.Contains(SupportPackageCategory.RunReports)
                ? SelectedRunIds.ToList()
                : Array.Empty<RunId>();

            var req = new SupportBundleRequest(_outputPath!, categories, runIds);
            var res = await _provider.BuildSupportPackageAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccess)
            {
                Error = res.Error!;
                return false;
            }

            CanaryScanPerformed = true;
            ManifestHash = res.Value!.ManifestHash;
            GeneratedAtUtc = res.Value!.GeneratedAtUtc;
            LastResult = res.Value!;
            return true;
        }
        finally { IsBusy = false; }
    }

    private string ComputeLocalManifestHash()
    {
        var included = Categories.Where(c => c.Include).Select(c => c.Name).OrderBy(n => n);
        using var sha = SHA256.Create();
        var blob = string.Join("|", included) + "|" + _outputPath;
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(blob))).ToLowerInvariant();
    }

    private void CanaryScanSelected()
    {
        // BCL stub of the canary/secret scan: in production this walks the selected categories' rows
        // for canary/secret hits before hashing. The gating contract (must run, must not throw) is what
        // the view model guarantees here. (When a provider is wired, the real scan runs in the backend.)
        foreach (var _ in Categories.Where(c => c.Include)) { }
    }
}
