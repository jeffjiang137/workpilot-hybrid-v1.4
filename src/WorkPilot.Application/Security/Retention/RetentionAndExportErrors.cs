using System.Collections.Generic;
using System.Collections.ObjectModel;
using WorkPilot.Contracts.Primitives;

namespace WorkPilot.Application.Security.Retention;

/// <summary>
/// Error catalog for retention / cleanup / export / support-package (LOG-005/006, SEC-106/107/108).
/// Registered globally so codes stay unique (AI dev rule §13).
/// </summary>
public sealed class RetentionAndExportErrors : FeatureErrorCatalog
{
    public override string Feature => "RetentionExport";

    public static readonly RetentionAndExportErrors Instance = new();

    public static readonly ErrorDefinition SettingsInvalid =
        new("RET_SETTINGS_INVALID", ErrorCategory.Validation, "Retention.SettingsInvalid", false);
    public static readonly ErrorDefinition CleanupFailed =
        new("RET_CLEANUP_FAILED", ErrorCategory.Internal, "Retention.CleanupFailed", false);
    public static readonly ErrorDefinition PackageInvalid =
        new("RET_PKG_INVALID", ErrorCategory.Validation, "Retention.PackageInvalid", false);
    public static readonly ErrorDefinition PackageTooLarge =
        new("RET_PKG_TOO_LARGE", ErrorCategory.Validation, "Retention.PackageTooLarge", false);
    public static readonly ErrorDefinition PackageCanaryHit =
        new("RET_PKG_CANARY", ErrorCategory.Internal, "Retention.PackageCanary", false);
    public static readonly ErrorDefinition PackageWriteFailed =
        new("RET_PKG_WRITE", ErrorCategory.Internal, "Retention.PackageWrite", false);
    public static readonly ErrorDefinition PackageRunLimit =
        new("RET_PKG_RUN_LIMIT", ErrorCategory.Validation, "Retention.PackageRunLimit", false);
    public static readonly ErrorDefinition RunReportNotFound =
        new("RET_RUN_REPORT_NOT_FOUND", ErrorCategory.Resource, "Retention.RunReportNotFound", false);
    public static readonly ErrorDefinition RunReportExportFailed =
        new("RET_RUN_REPORT_EXPORT", ErrorCategory.Internal, "Retention.RunReportExport", false);
    public static readonly ErrorDefinition RetentionStore =
        new("RET_STORE", ErrorCategory.Database, "Retention.Store", false);

    static RetentionAndExportErrors() => ErrorCatalog.Register(Instance);

    public override IReadOnlyList<ErrorDefinition> Definitions => new[]
    {
        SettingsInvalid, CleanupFailed, PackageInvalid, PackageTooLarge,
        PackageCanaryHit, PackageWriteFailed, PackageRunLimit, RunReportNotFound, RunReportExportFailed, RetentionStore
    };

    public static AppError SettingsInvalidError(string detail) =>
        Instance.Error(SettingsInvalid.Code, new Dictionary<string, string> { ["detail"] = detail });
    public static AppError CleanupFailedError(string detail) =>
        Instance.Error(CleanupFailed.Code, new Dictionary<string, string> { ["detail"] = detail });
    public static AppError PackageInvalidError(string reason) =>
        Instance.Error(PackageInvalid.Code, new Dictionary<string, string> { ["reason"] = reason });
    public static AppError PackageTooLargeError(long bytes) =>
        Instance.Error(PackageTooLarge.Code, new Dictionary<string, string> { ["bytes"] = bytes.ToString(System.Globalization.CultureInfo.InvariantCulture) });
    public static AppError PackageCanaryError(string file) =>
        Instance.Error(PackageCanaryHit.Code, new Dictionary<string, string> { ["file"] = file });
    public static AppError PackageWriteError(string detail) =>
        Instance.Error(PackageWriteFailed.Code, new Dictionary<string, string> { ["detail"] = detail });
    public static AppError PackageRunLimitError(int requested) =>
        Instance.Error(PackageRunLimit.Code, new Dictionary<string, string> { ["requested"] = requested.ToString(System.Globalization.CultureInfo.InvariantCulture) });
    public static AppError RunReportNotFoundError(string id) =>
        Instance.Error(RunReportNotFound.Code, new Dictionary<string, string> { ["id"] = id });
    public static AppError RunReportExportError(string detail) =>
        Instance.Error(RunReportExportFailed.Code, new Dictionary<string, string> { ["detail"] = detail });
    public static AppError RetentionStoreError(string detail) =>
        Instance.Error(RetentionStore.Code, new Dictionary<string, string> { ["detail"] = detail });
}
