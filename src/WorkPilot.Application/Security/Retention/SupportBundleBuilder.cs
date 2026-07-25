using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Application.Automation.Run;
using WorkPilot.Application.Permission.Policy;
using WorkPilot.Application.Security.Governance;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation.Run.Redaction;
using WorkPilot.Domain.PermissionGovernance;
using WorkPilot.Domain.Security;
using WorkPilot.Domain.Security.Audit;
using WorkPilot.Domain.Security.Retention;

namespace WorkPilot.Application.Security.Retention;

/// <summary>
/// Builds a privacy-safe support package (doc 05 §10.2, LOG-006, SEC-108).
/// <list type="bullet">
///   <item><description>Writes into a staging directory, then zips to a temp file.</description></item>
///   <item><description>Verifies the zip is ≤ <see cref="Limits.V1_5.SupportBundleMaxBytes"/> (25 MiB).</description></item>
///   <item><description>Runs a canary/secret scan over every zipped entry; a surviving canary fails the build (LOG-A05/SEC-A14).</description></item>
///   <item><description>Emits <c>manifest.json</c> with a SHA-256 per file plus a package-level manifest hash.</description></item>
///   <item><description>Atomic-moves the zip to the chosen local path; staging + temp zip are cleaned on success or failure/cancel.</description></item>
///   <item><description>Never includes the DB file, DPAPI secrets, skill bodies, project paths, or model/tool bodies — only the specific JSON artefacts below.</description></item>
/// </list>
/// </summary>
public sealed class SupportBundleBuilder
{
    private readonly IIncidentStore _incidents;
    private readonly IAuditLogStore _audit;
    private readonly ISourceGovernanceBackend _sourceBackend;
    private readonly IGrantStore _grants;
    private readonly IRunRepository _runs;
    private readonly IRunReportExporter _runReportExporter;
    private readonly IRetentionSettingsStore? _settings;
    private readonly IAuditIntegrityMonitor _integrity;
    private readonly IDiagnosticLogDirectory _diagnosticDir;
    private readonly IAppInfo _appInfo;
    private readonly ISecretMatcher? _matcher;
    private readonly ISet<string> _canaryTokens;
    private readonly IClock _clock;

    public SupportBundleBuilder(
        IIncidentStore incidents,
        IAuditLogStore audit,
        ISourceGovernanceBackend sourceBackend,
        IGrantStore grants,
        IRunRepository runs,
        IRunReportExporter runReportExporter,
        IAuditIntegrityMonitor integrity,
        IDiagnosticLogDirectory diagnosticDir,
        IAppInfo appInfo,
        ISet<string> canaryTokens,
        IClock clock,
        IRetentionSettingsStore? settings = null,
        ISecretMatcher? matcher = null)
    {
        _incidents = incidents;
        _audit = audit;
        _sourceBackend = sourceBackend;
        _grants = grants;
        _runs = runs;
        _runReportExporter = runReportExporter;
        _integrity = integrity;
        _diagnosticDir = diagnosticDir;
        _appInfo = appInfo;
        _canaryTokens = canaryTokens ?? new HashSet<string>();
        _clock = clock;
        _settings = settings;
        _matcher = matcher;
    }

    public async Task<Result<SupportBundleResult>> BuildAsync(SupportBundleRequest request, CancellationToken ct = default)
    {
        var validation = request.Validate();
        if (!validation.IsSuccess) return Result<SupportBundleResult>.Fail(validation.Error!);

        var staging = Path.Combine(Path.GetTempPath(), "workpilot-support-" + Guid.NewGuid().ToString("N"));
        var tempZip = Path.Combine(Path.GetTempPath(), "workpilot-support-" + Guid.NewGuid().ToString("N") + ".zip");
        var cleanup = true; // becomes false once the zip is successfully moved to the output path
        try
        {
            Directory.CreateDirectory(staging);
            var included = new List<string>();

            // Always-on support baseline (LOG-006): re-redacted diagnostics + integrity + meta.
            var diag = await WriteDiagnosticsAsync(staging, ct).ConfigureAwait(false);
            if (!diag.IsSuccess) return Result<SupportBundleResult>.Fail(diag.Error!);
            included.Add("Diagnostics");

            foreach (var category in request.Categories)
            {
                ct.ThrowIfCancellationRequested();
                switch (category)
                {
                    case SupportPackageCategory.Incidents:
                        await WriteJsonAsync(staging, "incidents.json",
                            await _incidents.ListAsync(null, Limits.V1_5.SupportBundleMaxIncidents, ct), ct).ConfigureAwait(false);
                        break;
                    case SupportPackageCategory.AuditLog:
                        var auditAll = await _audit.GetAllAsync(ct).ConfigureAwait(false);
                        await WriteJsonAsync(staging, "audit.json",
                            auditAll.Take(Limits.V1_5.SupportBundleMaxAuditEntries), ct).ConfigureAwait(false);
                        break;
                    case SupportPackageCategory.SourceHealth:
                        var health = await _sourceBackend.ListHealthAsync(ct).ConfigureAwait(false);
                        if (health.IsSuccess)
                            await WriteJsonAsync(staging, "source_health.json", health.Value, ct).ConfigureAwait(false);
                        else
                            await WriteJsonAsync(staging, "source_health.json",
                                new { degraded = true, detail = health.Error?.Code }, ct).ConfigureAwait(false);
                        break;
                    case SupportPackageCategory.Policy:
                        var active = await _grants.ListActiveAsync(_clock.UtcNow, ct).ConfigureAwait(false);
                        await WriteJsonAsync(staging, "policy.json",
                            active.IsSuccess ? active.Value : Array.Empty<PolicyGrant>(), ct).ConfigureAwait(false);
                        break;
                    case SupportPackageCategory.Configuration:
                        await WriteJsonAsync(staging, "configuration.json", await BuildConfigurationAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                        break;
                    case SupportPackageCategory.RunReports:
                        await WriteRunReportsAsync(staging, request.RunIds, ct).ConfigureAwait(false);
                        break;
                }
                included.Add(category.ToString());
            }

            await WriteJsonAsync(staging, "meta.json", BuildMeta(included), ct).ConfigureAwait(false);
            await WriteJsonAsync(staging, "integrity.json", await _integrity.VerifyAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);

            var manifestHash = await WriteManifestAsync(staging, ct).ConfigureAwait(false);

            ZipStaging(staging, tempZip);

            var size = new FileInfo(tempZip).Length;
            if (size > Limits.V1_5.SupportBundleMaxBytes)
            {
                // Do not publish an oversized package. The temp zip is removed by the finally block.
                return Result<SupportBundleResult>.Fail(RetentionAndExportErrors.PackageTooLargeError(size));
            }

            var canary = ScanZipForCanary(tempZip);
            if (canary is not null)
            {
                cleanup = false;
                TryDelete(request.OutputPath);
                return Result<SupportBundleResult>.Fail(RetentionAndExportErrors.PackageCanaryError(canary));
            }

            // Atomic move to the operator's chosen local path.
            File.Move(tempZip, request.OutputPath, overwrite: true);
            cleanup = false; // source already consumed by the move

            return Result<SupportBundleResult>.Ok(new SupportBundleResult(
                OutputPath: request.OutputPath,
                ManifestHash: manifestHash,
                TotalBytes: size,
                FileCount: CountFiles(staging),
                GeneratedAtUtc: _clock.UtcNow,
                IncludedCategories: included));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result<SupportBundleResult>.Fail(RetentionAndExportErrors.PackageWriteError(ex.Message));
        }
        finally
        {
            TryDelete(staging);
            if (cleanup) TryDelete(tempZip);
        }
    }

    private async Task WriteRunReportsAsync(string staging, IReadOnlyList<RunId> ids, CancellationToken ct)
    {
        var dir = Path.Combine(staging, "runs");
        Directory.CreateDirectory(dir);
        foreach (var id in ids.Take(Limits.V1_5.SupportBundleMaxRunReports))
        {
            var report = await _runReportExporter.BuildAsync(id, ct).ConfigureAwait(false);
            if (report.IsSuccess)
                await WriteJsonAsync(dir, id.ToString() + ".json", report.Value, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Writes the most recent diagnostic logs, re-redacted (LOG-006). A surviving canary fails the build.</summary>
    private async Task<Result> WriteDiagnosticsAsync(string staging, CancellationToken ct)
    {
        var dir = Path.Combine(staging, "diagnostics");
        Directory.CreateDirectory(dir);
        if (!Directory.Exists(_diagnosticDir.Directory)) return Result.Success();

        var files = Directory.GetFiles(_diagnosticDir.Directory)
            .Where(f => Path.GetFileName(f).StartsWith(_diagnosticDir.BaseName, StringComparison.Ordinal) && f.EndsWith(".log", StringComparison.Ordinal))
            .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
            .Take(Limits.V1_5.SupportBundleMaxDiagnosticFiles);

        var idx = 0;
        foreach (var f in files)
        {
            var text = await File.ReadAllTextAsync(f, ct).ConfigureAwait(false);
            var redacted = RedactionPipeline.RedactSerialized(text, _matcher, _canaryTokens, releaseMode: true);
            if (redacted.HasViolation)
                return Result.Failure(RetentionAndExportErrors.PackageCanaryError(Path.GetFileName(f)));
            await File.WriteAllTextAsync(Path.Combine(dir, idx + ".log"), redacted.Value, Encoding.UTF8, ct).ConfigureAwait(false);
            idx++;
        }
        return Result.Success();
    }

    private async Task<object> BuildConfigurationAsync(CancellationToken ct)
    {
        RetentionPolicy policy = RetentionPolicy.Default;
        if (_settings is not null)
        {
            var get = await _settings.GetAsync(ct).ConfigureAwait(false);
            if (get.IsSuccess) policy = get.Value!.Policy;
        }
        return new
        {
            app_version = _appInfo.AppVersion,
            os_version = _appInfo.OsVersion,
            architecture = _appInfo.Architecture,
            database_schema_version = _appInfo.DatabaseSchemaVersion,
            retention_policy = policy
        };
    }

    private object BuildMeta(IReadOnlyList<string> included) => new
    {
        generated_at_utc = _clock.UtcNow.UtcDateTime.ToString("O"),
        app_version = _appInfo.AppVersion,
        os_version = _appInfo.OsVersion,
        architecture = _appInfo.Architecture,
        database_schema_version = _appInfo.DatabaseSchemaVersion,
        included_categories = included
    };

    private static async Task WriteJsonAsync(string dir, string fileName, object? content, CancellationToken ct)
    {
        var path = Path.Combine(dir, fileName);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(content, new JsonSerializerOptions { WriteIndented = false }), Encoding.UTF8, ct).ConfigureAwait(false);
    }

    private static void ZipStaging(string staging, string zipPath)
    {
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
        {
            var relative = GetRelativePath(staging, file).Replace('\\', '/');
            zip.CreateEntryFromFile(file, relative);
        }
    }

    private static string GetRelativePath(string baseDir, string file)
    {
        var baseUri = new Uri(baseDir.EndsWith(Path.DirectorySeparatorChar.ToString()) ? baseDir : baseDir + Path.DirectorySeparatorChar);
        var fileUri = new Uri(file);
        return Uri.UnescapeDataString(baseUri.MakeRelativeUri(fileUri).ToString());
    }

    private async Task<string> WriteManifestAsync(string staging, CancellationToken ct)
    {
        var entries = new List<object>();
        var sb = new StringBuilder();
        foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            var relative = GetRelativePath(staging, file).Replace('\\', '/');
            var hash = await Sha256FileAsync(file, ct).ConfigureAwait(false);
            entries.Add(new { path = relative, sha256 = hash });
            sb.Append(relative).Append('|').Append(hash).Append('\n');
        }
        await File.WriteAllTextAsync(Path.Combine(staging, "manifest.json"),
            JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = false }), Encoding.UTF8, ct).ConfigureAwait(false);

        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    private static async Task<string> Sha256FileAsync(string path, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        await using var fs = File.OpenRead(path);
        var bytes = await sha.ComputeHashAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private string? ScanZipForCanary(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            if (entry.Length > 64 * 1024 * 1024) continue; // skip absurd entries
            using var reader = new StreamReader(entry.Open());
            var text = reader.ReadToEnd();
            var result = RedactionPipeline.RedactSerialized(text, _matcher, _canaryTokens, releaseMode: true);
            if (result.HasViolation) return entry.FullName;
        }
        return null;
    }

    private static int CountFiles(string dir) => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Count();

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort cleanup */ }
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { /* best-effort cleanup */ }
    }
}
