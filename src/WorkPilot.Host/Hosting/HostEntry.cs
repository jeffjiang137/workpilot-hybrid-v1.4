using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Host.Core.Scheduling;
using WorkPilot.Infrastructure.Data;

namespace WorkPilot.Host.Hosting;

/// <summary>
/// Composition seam for the background Host lifecycle. T09's real <c>Program</c> (or the App's
/// bootstrapper) calls <see cref="RunAsync"/> with a concrete <see cref="ITaskScheduler"/>,
/// <see cref="ISidResolver"/>, an <see cref="IClock"/>, and the materialization <see cref="IHostWorker"/>.
/// T08 delivers the lifecycle (mutex + heartbeat + graceful stop) with a null worker so the Host
/// is a correctly-behaving, separately-scheduled process even before the worker exists.
/// </summary>
public static class HostEntry
{
    public static async Task RunAsync(
        string appId,
        ITaskScheduler scheduler,
        ISidResolver sidResolver,
        IClock clock,
        IHostWorker? worker,
        CancellationToken externalStop)
    {
        _ = sidResolver ?? throw new ArgumentNullException(nameof(sidResolver));

        // T23 (MIG-A07): the Host never migrates and only opens an exactly-matching schema. Refuse and
        // exit cleanly when the database is un-migrated, older, or newer than this binary; the App is
        // responsible for migrations.
        await EnsureSchemaCompatibleAsync(clock, externalStop);

        var runner = new HostRunner(scheduler, clock, appId, worker);
        if (!runner.TryAcquireSingleInstance())
            return; // another Host instance already owns the mutex; nothing to do

        try
        {
            runner.Start();
            try
            {
                await Task.Delay(Timeout.Infinite, externalStop);
            }
            catch (OperationCanceledException) when (externalStop.IsCancellationRequested)
            {
                // stop signal received
            }

            await runner.StopAsync();
        }
        finally
        {
            runner.ReleaseMutex();
        }
    }

    /// <summary>
    /// Opens the shared WorkPilot database read-only and runs the schema handshake as the Host.
    /// The Host must not migrate and only accepts an exactly-matching schema, so any mismatch makes
    /// it exit before acquiring the instance mutex or starting the worker (MIG-A07).
    /// </summary>
    private static async Task EnsureSchemaCompatibleAsync(IClock clock, CancellationToken externalStop)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkPilot");
        var dbPath = Path.Combine(directory, "workpilot.db");
        if (!File.Exists(dbPath))
            return; // App will create/migrate on next launch; nothing for the Host to do yet.

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(externalStop);

        var handshake = new SchemaUpgradeHandshake(
            V15DatabaseMigrator.LatestVersion,
            V15DatabaseMigrator.LatestVersion,
            new SqliteSchemaVersionProbe(),
            new V15DatabaseMigrator(clock));
        var result = await handshake.PerformAsync(connection, isHost: true, externalStop);
        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"Host 检测到数据库架构不兼容（{result.Compatibility.Kind}: {result.Compatibility.MessageKey}），已退出。请先启动主程序完成升级。");
        }
    }
}
