using System.IO;
using WorkPilot.Contracts.Primitives;

namespace WorkPilot.Host.Core.Scheduling;

/// <summary>
/// Anti-tamper guard for the registered Host executable path (RUN-001 / T08 "路径篡改" test).
/// The scheduled task must launch exactly the expected Host binary from the expected install
/// directory; any deviation (relative path, missing file, or a path that escapes the install
/// root) is rejected so a tampered task cannot escalate or run an attacker binary.
/// </summary>
public sealed class ExecutablePathValidator
{
    /// <summary>
    /// Validate that <paramref name="candidate"/> is a permitted Host executable.
    /// </summary>
    /// <param name="candidate">The candidate executable path from the resolved install location.</param>
    /// <param name="installRoot">The trusted install directory the binary must live under (absolute).</param>
    public Result Validate(string? candidate, string installRoot)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return Result.Failure(SchedulerErrors.ExecutablePathInvalidError("路径为空"));
        if (string.IsNullOrWhiteSpace(installRoot))
            return Result.Failure(SchedulerErrors.ExecutablePathInvalidError("安装根目录为空"));

        // Must be absolute and a real file on disk.
        if (!Path.IsPathRooted(candidate))
            return Result.Failure(SchedulerErrors.ExecutablePathInvalidError("必须为绝对路径"));

        string fullCandidate;
        string fullRoot;
        try
        {
            fullCandidate = Path.GetFullPath(candidate!);
            fullRoot = Path.GetFullPath(installRoot);
        }
        catch (IOException ex)
        {
            return Result.Failure(SchedulerErrors.ExecutablePathInvalidError(ex.Message));
        }

        if (!File.Exists(fullCandidate))
            return Result.Failure(SchedulerErrors.ExecutablePathInvalidError("文件不存在"));

        // Must be contained within the trusted install root (no ".." escape, no UNC/alt stream tricks).
        var rootWithSep = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        if (!fullCandidate.StartsWith(rootWithSep, PathComparison()))
            return Result.Failure(SchedulerErrors.ExecutablePathInvalidError("不在受信任的安装目录内"));

        if (!HasHostExecutableName(fullCandidate))
            return Result.Failure(SchedulerErrors.ExecutablePathInvalidError("不是受信任的宿主可执行文件名"));

        return Result.Success();
    }

    private static bool HasHostExecutableName(string fullPath)
    {
        var name = Path.GetFileName(fullPath);
        return string.Equals(name, "WorkPilot.Host.exe", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "WorkPilot.Host.dll", System.StringComparison.OrdinalIgnoreCase);
    }

    private static System.StringComparison PathComparison() => System.StringComparison.OrdinalIgnoreCase;
}
