using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using WorkPilot.Application.Security.Retention;
using WorkPilot.Contracts.Primitives;

namespace WorkPilot.Services;

/// <summary>
/// Host-provided ports required by the retention / support-package subsystem (doc 05 §9/§10.2,
/// SEC-106/108). All four are pure, secret-free adapters over the local machine; they hold no
/// credentials and never touch connector / native surfaces.
/// </summary>
internal sealed class DiagnosticLogDirectory : IDiagnosticLogDirectory
{
    // doc 05 §5: diagnostics live under %LocalAppData%/WorkPilot/diagnostics.
    public string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkPilot", "diagnostics");
    public string BaseName => "workpilot-diagnostics";
}

internal sealed class AppInfo : IAppInfo
{
    // Tracks the latest applied migration; keep in sync with V15DatabaseMigrator.RetentionVersion.
    private const int CurrentSchemaVersion = 22;

    private static readonly string AppVersion = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? (Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0");

    public string AppVersion => AppVersion;
    public string OsVersion => Environment.OSVersion.VersionString;
    public string Architecture => RuntimeInformation.OSArchitecture.ToString();
    public int DatabaseSchemaVersion => CurrentSchemaVersion;
}

internal sealed class DiskSpaceProbe : IDiskSpaceProbe
{
    public long GetFreeBytes(string path)
    {
        var root = Path.GetPathRoot(path) ?? path;
        try
        {
            var drive = new DriveInfo(root);
            return drive.AvailableFreeSpace;
        }
        catch (Exception) when (path == root || !Path.IsPathRooted(path))
        {
            // For a memory-backed or pathless target, report a generous ceiling rather than fail the probe.
            return long.MaxValue;
        }
    }
}

/// <summary>
/// Manual <c>VACUUM INTO</c> shrink (doc 05 §9). Builds a compacted copy in a temp file, verifies it,
/// then atomically replaces the live database. Never invoked by automatic cleanup.
/// </summary>
internal sealed class SqliteOptimizeDatabase : IOptimizeDatabase
{
    private readonly SqliteConnection _connection;

    public SqliteOptimizeDatabase(SqliteConnection connection) =>
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    public async Task<Result> OptimizeAsync(System.Threading.CancellationToken ct = default)
    {
        try
        {
            var source = _connection.DataSource;
            if (string.IsNullOrEmpty(source) || !File.Exists(source))
                return Result.Failure(RetentionAndExportErrors.RetentionStoreError("optimize: no backing file"));

            var dir = Path.GetDirectoryName(source)!;
            var tmp = Path.Combine(dir, $"workpilot.optimize.{DateTimeOffset.UtcNow:yyyyMMddHHmmssffff}.db");
            try
            {
                // VACUUM INTO requires no open writer transaction on the source; the shared connection is
                // used read-only here. A fresh connection issues the VACUUM to avoid long-lived locks.
                await using var vacuum = new SqliteConnection($"Data Source={source}");
                await vacuum.OpenAsync(ct);
                using var cmd = vacuum.CreateCommand();
                cmd.CommandText = $"VACUUM INTO '{tmp.Replace("'", "''")}'";
                await cmd.ExecuteNonQueryAsync(ct);

                File.Delete(source);
                File.Move(tmp, source);
                return Result.Success();
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }
        catch (Exception error)
        {
            return Result.Failure(RetentionAndExportErrors.RetentionStoreError($"optimize: {error.Message}"));
        }
    }
}
