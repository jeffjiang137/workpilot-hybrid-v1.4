using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;

namespace WorkPilot.Application.Security.Retention;

/// <summary>Request to build a support package (doc 05 §10.2, SEC-108).</summary>
public sealed record SupportBundleRequest(
    string OutputPath,
    ISet<SupportPackageCategory> Categories,
    IReadOnlyList<RunId> RunIds)
{
    /// <summary>Validates the request before any I/O (SEC-108 gating).</summary>
    public Result Validate()
    {
        if (string.IsNullOrWhiteSpace(OutputPath))
            return Result.Failure(RetentionAndExportErrors.PackageInvalidError("no_output_path"));
        if (Categories is null || Categories.Count == 0)
            return Result.Failure(RetentionAndExportErrors.PackageInvalidError("no_category"));
        if (Categories.Contains(SupportPackageCategory.RunReports) && (RunIds is null || RunIds.Count == 0))
            return Result.Failure(RetentionAndExportErrors.PackageInvalidError("run_reports_requires_selection"));
        if (RunIds is not null && RunIds.Count > Limits.V1_5.SupportBundleMaxRunReports)
            return Result.Failure(RetentionAndExportErrors.PackageRunLimitError(RunIds.Count));
        return Result.Success();
    }
}
