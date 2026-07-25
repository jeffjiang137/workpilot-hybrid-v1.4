using System.Globalization;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Infrastructure.Clock;

namespace WorkPilot.Infrastructure.Data;

/// <summary>
/// V1.5 Migration 017：持久化自动化定义、修订与触发器。
/// 把 V1.4 的 <c>automations</c> 表无损转换为新的
/// <c>automation_definitions</c> / <c>automation_revisions</c> / <c>automation_schedules</c> 结构，
/// 原表重命名为 <c>automations_v12_legacy</c> 并保留为只读历史（产品代码不再访问）。
/// 本迁移器位于 Infrastructure 层，仅依赖 <see cref="SqliteConnection"/> 与 Contracts，不引用 App 层。
/// </summary>
public sealed class V15DatabaseMigrator
{
    /// <summary>Authoritative latest applied migration version. The App/Host handshake compares the
    /// database's MAX(schema_migrations.version) against this value (T23, MIG-A06/A07).</summary>
    public const int LatestVersion = 22;

    private const int Version = 17;
    private const string MigrationName = "017_v15_automation_definitions";
    private const int RunVersion = 18;
    private const string RunMigrationName = "018_v15_durable_runs";
    private const int PolicyVersion = 19;
    private const string PolicyMigrationName = "019_v15_policy_governance";
    private const int SecurityVersion = 20;
    private const string SecurityMigrationName = "020_v15_security";
    private const int StateVersion = 21;
    private const string StateMigrationName = "021_v15_security_state";
    private const int RetentionVersion = 22;
    private const string RetentionMigrationName = "022_v15_retention_settings";
    private const string LegacyTable = "automations_v12_legacy";
    private const int MaxIntervalMinutes = 10080; // 7 天
    private const string DefaultTriggerId = "interval_1";
    private const string AgentNodeId = "agent_prompt_1";

    private readonly IClock _clock;
    private readonly Action<string>? _log;

    public V15DatabaseMigrator(IClock? clock = null, Action<string>? log = null)
    {
        _clock = clock ?? new SystemClock();
        _log = log;
    }

    public async Task InitializeAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        await using (var fk = connection.CreateCommand())
        {
            fk.CommandText = "PRAGMA foreign_keys=ON";
            await fk.ExecuteNonQueryAsync(cancellationToken);
        }

        await EnsureMigration017Async(connection, cancellationToken);
        await EnsureMigration018Async(connection, cancellationToken);
        await EnsureMigration019Async(connection, cancellationToken);
        await EnsureMigration020Async(connection, cancellationToken);
        await EnsureMigration021Async(connection, cancellationToken);
        await EnsureMigration022Async(connection, cancellationToken);
    }

    /// <summary>Idempotently applies Migration 017 (legacy automations → definitions/revisions/schedules).</summary>
    private async Task EnsureMigration017Async(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var existing = await GetChecksumAsync(connection, Version, cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing, Sha256(Migration017Ddl), StringComparison.Ordinal))
                throw new InvalidDataException($"迁移 {Version:000} 校验和不一致，启动已停止");
            return; // 幂等：已迁移
        }

        var backupPath = CreateBackup(connection);
        try
        {
            await Apply017Async(connection, cancellationToken);
            await VerifyNoOrphanRevisionAsync(connection, cancellationToken);
            _log?.Invoke("Migration 017 applied: automation definitions created from legacy automations.");
        }
        catch
        {
            if (backupPath is not null) RestoreBackup(connection, backupPath);
            throw;
        }
    }

    /// <summary>
    /// Idempotently applies Migration 018 (durable run / step / event / snapshot / occurrence tables).
    /// Purely additive DDL executed in a single statement, so it is atomic and needs no backup.
    /// </summary>
    private async Task EnsureMigration018Async(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var existing = await GetChecksumAsync(connection, RunVersion, cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing, Sha256(Migration018Ddl), StringComparison.Ordinal))
                throw new InvalidDataException($"迁移 {RunVersion:000} 校验和不一致，启动已停止");
            return; // 幂等：已迁移
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var ddl = connection.CreateCommand();
            ddl.CommandText = Migration018Ddl;
            ddl.Transaction = (SqliteTransaction)transaction;
            await ddl.ExecuteNonQueryAsync(cancellationToken);

            await RecordMigrationAsync(connection, transaction, RunVersion, RunMigrationName, Migration018Ddl, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            _log?.Invoke("Migration 018 applied: durable run storage created.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Idempotently applies Migration 019 (policy governance storage: documents / versions /
    /// statements / grants / consent receipts / audit). Purely additive DDL executed atomically.
    /// The default minimum-permission policy itself is NOT seeded here — seeding happens via
    /// <c>EnsureDefaultPolicyAsync</c> (application bootstrap) so the DDL stays checksum-stable and
    /// the upgrade never expands V1.4 permissions (T16 DoD).
    /// </summary>
    private async Task EnsureMigration019Async(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var existing = await GetChecksumAsync(connection, PolicyVersion, cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing, Sha256(Migration019Ddl), StringComparison.Ordinal))
                throw new InvalidDataException($"迁移 {PolicyVersion:000} 校验和不一致，启动已停止");
            return; // 幂等：已迁移
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var ddl = connection.CreateCommand();
            ddl.CommandText = Migration019Ddl;
            ddl.Transaction = (SqliteTransaction)transaction;
            await ddl.ExecuteNonQueryAsync(cancellationToken);

            await RecordMigrationAsync(connection, transaction, PolicyVersion, PolicyMigrationName, Migration019Ddl, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            _log?.Invoke("Migration 019 applied: policy governance storage created.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Creates the 017 automation tables (without the legacy RENAME/convert/checksum steps) on a
    /// fresh database. Used by tests and by any bootstrap that needs the schema without legacy data.
    /// The DDL is sliced from <see cref="Migration017Ddl"/> at the first CREATE TABLE so it never
    /// drifts from the production migration.
    /// </summary>
    public async Task CreateTablesAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        await using (var fk = connection.CreateCommand())
        {
            fk.CommandText = "PRAGMA foreign_keys=ON";
            await fk.ExecuteNonQueryAsync(cancellationToken);
        }

        var schemaSql = Migration017Ddl.Substring(Migration017Ddl.IndexOf("CREATE TABLE", StringComparison.Ordinal));
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var cmd = connection.CreateCommand();
            cmd.Transaction = (SqliteTransaction)transaction;
            cmd.CommandText = schemaSql;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Creates the 018 durable-run tables (without the legacy RENAME/convert steps) on a fresh
    /// database. The DDL is sliced from <see cref="Migration018Ddl"/> at the first CREATE TABLE so it
    /// never drifts from the production migration. Prerequisite tables (spaces, expert_revisions,
    /// the 017 automation tables) must already exist — call <see cref="CreateTablesAsync"/> first
    /// and seed spaces/expert_revisions in the test bootstrap.
    /// </summary>
    public async Task CreateRunTablesAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        await using (var fk = connection.CreateCommand())
        {
            fk.CommandText = "PRAGMA foreign_keys=ON";
            await fk.ExecuteNonQueryAsync(cancellationToken);
        }

        var schemaSql = Migration018Ddl.Substring(Migration018Ddl.IndexOf("CREATE TABLE", StringComparison.Ordinal));
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var cmd = connection.CreateCommand();
            cmd.Transaction = (SqliteTransaction)transaction;
            cmd.CommandText = schemaSql;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Creates the 019 policy-governance tables (without legacy steps) on a fresh database. The DDL
    /// is sliced from <see cref="Migration019Ddl"/> at the first CREATE TABLE so it never drifts from
    /// the production migration. Call <see cref="CreateTablesAsync"/> / <see cref="CreateRunTablesAsync"/>
    /// first so the policy FK targets (spaces, expert_revisions, automation_revisions, runs, steps)
    /// exist when seeding default policy or consent receipts.
    /// </summary>
    public async Task CreatePolicyTablesAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        await using (var fk = connection.CreateCommand())
        {
            fk.CommandText = "PRAGMA foreign_keys=ON";
            await fk.ExecuteNonQueryAsync(cancellationToken);
        }

        var schemaSql = Migration019Ddl.Substring(Migration019Ddl.IndexOf("CREATE TABLE", StringComparison.Ordinal));
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var cmd = connection.CreateCommand();
            cmd.Transaction = (SqliteTransaction)transaction;
            cmd.CommandText = schemaSql;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Idempotently applies Migration 020 (security storage: events / incidents / tamper-evident audit
    /// log / detector action ledger). Purely additive DDL executed atomically. No default data is
    /// seeded here — seeding is an application-bootstrap concern so the DDL stays checksum-stable.
    /// </summary>
    private async Task EnsureMigration020Async(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var existing = await GetChecksumAsync(connection, SecurityVersion, cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing, Sha256(Migration020Ddl), StringComparison.Ordinal))
                throw new InvalidDataException($"迁移 {SecurityVersion:000} 校验和不一致，启动已停止");
            return; // 幂等：已迁移
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var ddl = connection.CreateCommand();
            ddl.CommandText = Migration020Ddl;
            ddl.Transaction = (SqliteTransaction)transaction;
            await ddl.ExecuteNonQueryAsync(cancellationToken);

            await RecordMigrationAsync(connection, transaction, SecurityVersion, SecurityMigrationName, Migration020Ddl, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            _log?.Invoke("Migration 020 applied: security storage created.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Creates the 020 security tables (without legacy steps) on a fresh database. The DDL is sliced
    /// from <see cref="Migration020Ddl"/> at the first CREATE TABLE so it never drifts from the
    /// production migration. The security tables have NO cross-migration foreign keys, so this is safe
    /// to call standalone (tests create only the security schema).
    /// </summary>
    public async Task CreateSecurityTablesAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        await using (var fk = connection.CreateCommand())
        {
            fk.CommandText = "PRAGMA foreign_keys=ON";
            await fk.ExecuteNonQueryAsync(cancellationToken);
        }

        var schemaSql = Migration020Ddl.Substring(Migration020Ddl.IndexOf("CREATE TABLE", StringComparison.Ordinal));
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var cmd = connection.CreateCommand();
            cmd.Transaction = (SqliteTransaction)transaction;
            cmd.CommandText = schemaSql;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Idempotently applies Migration 021 (security governance state: <c>security_state</c> key/value
    /// flags for <c>ISecurityStateStore</c>, and the single-row <c>revocation_epoch</c> for the
    /// process-wide <see cref="WorkPilot.Application.Permission.Policy.IRevocationEpoch"/>). Purely
    /// additive DDL executed atomically. The emergency-stop flag and revocation epoch are governance
    /// counters only — never secrets (doc 06 §6.4).
    /// </summary>
    private async Task EnsureMigration021Async(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var existing = await GetChecksumAsync(connection, StateVersion, cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing, Sha256(Migration021Ddl), StringComparison.Ordinal))
                throw new InvalidDataException($"迁移 {StateVersion:000} 校验和不一致，启动已停止");
            return; // 幂等：已迁移
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var ddl = connection.CreateCommand();
            ddl.CommandText = Migration021Ddl;
            ddl.Transaction = (SqliteTransaction)transaction;
            await ddl.ExecuteNonQueryAsync(cancellationToken);

            // Seed the revocation epoch at 0 so Current/Bump have a stable starting row.
            var seed = connection.CreateCommand();
            seed.Transaction = (SqliteTransaction)transaction;
            seed.CommandText = "INSERT INTO revocation_epoch(epoch) VALUES(0)";
            await seed.ExecuteNonQueryAsync(cancellationToken);

            await RecordMigrationAsync(connection, transaction, StateVersion, StateMigrationName, Migration021Ddl, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            _log?.Invoke("Migration 021 applied: security state + revocation epoch created.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Creates the 021 security-state tables (without legacy steps) on a fresh database. The DDL is
    /// sliced from <see cref="Migration021Ddl"/> at the first CREATE TABLE so it never drifts from the
    /// production migration. Safe to call standalone (no cross-migration foreign keys).
    /// </summary>
    public async Task CreateSecurityStateTablesAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        await using (var fk = connection.CreateCommand())
        {
            fk.CommandText = "PRAGMA foreign_keys=ON";
            await fk.ExecuteNonQueryAsync(cancellationToken);
        }

        var schemaSql = Migration021Ddl.Substring(Migration021Ddl.IndexOf("CREATE TABLE", StringComparison.Ordinal));
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var cmd = connection.CreateCommand();
            cmd.Transaction = (SqliteTransaction)transaction;
            cmd.CommandText = schemaSql;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Idempotently applies Migration 022 (retention settings singleton, doc 05 §9 / SEC-106). Purely
    /// additive DDL executed atomically. No default row is seeded here — the store returns
    /// <see cref="RetentionSettings.Default"/> when the singleton is absent (fail-safe, never disables
    /// cleanup). Frozen per spec — the SHA-256 of this string is recorded at apply time and verified on
    /// startup.
    /// </summary>
    private async Task EnsureMigration022Async(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var existing = await GetChecksumAsync(connection, RetentionVersion, cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing, Sha256(Migration022Ddl), StringComparison.Ordinal))
                throw new InvalidDataException($"迁移 {RetentionVersion:000} 校验和不一致，启动已停止");
            return; // 幂等：已迁移
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var ddl = connection.CreateCommand();
            ddl.CommandText = Migration022Ddl;
            ddl.Transaction = (SqliteTransaction)transaction;
            await ddl.ExecuteNonQueryAsync(cancellationToken);

            await RecordMigrationAsync(connection, transaction, RetentionVersion, RetentionMigrationName, Migration022Ddl, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            _log?.Invoke("Migration 022 applied: retention settings singleton created.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Creates the 022 retention-settings table (without legacy steps) on a fresh database. The DDL is
    /// sliced from <see cref="Migration022Ddl"/> at the first CREATE TABLE so it never drifts from the
    /// production migration. Safe to call standalone (no cross-migration foreign keys).
    /// </summary>
    public async Task CreateRetentionTablesAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        await using (var fk = connection.CreateCommand())
        {
            fk.CommandText = "PRAGMA foreign_keys=ON";
            await fk.ExecuteNonQueryAsync(cancellationToken);
        }

        var schemaSql = Migration022Ddl.Substring(Migration022Ddl.IndexOf("CREATE TABLE", StringComparison.Ordinal));
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var cmd = connection.CreateCommand();
            cmd.Transaction = (SqliteTransaction)transaction;
            cmd.CommandText = schemaSql;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task Apply017Async(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var ddl = connection.CreateCommand();
            ddl.CommandText = Migration017Ddl;
            ddl.Transaction = (SqliteTransaction)transaction;
            await ddl.ExecuteNonQueryAsync(cancellationToken);

            var spaceId = await ResolveSpaceIdAsync(connection, transaction, cancellationToken);
            var now = _clock.UtcNow.ToString("O");

            var legacyRows = await ReadLegacyRowsAsync(connection, transaction, cancellationToken);
            foreach (var row in legacyRows)
                await ConvertLegacyRowAsync(connection, transaction, row, spaceId, now, cancellationToken);

            var record = connection.CreateCommand();
            record.CommandText = "INSERT INTO schema_migrations(version,name,applied_at,checksum) VALUES($version,$name,$now,$checksum)";
            record.Transaction = (SqliteTransaction)transaction;
            record.Parameters.AddWithValue("$version", Version);
            record.Parameters.AddWithValue("$name", MigrationName);
            record.Parameters.AddWithValue("$now", now);
            record.Parameters.AddWithValue("$checksum", Sha256(Migration017Ddl));
            await record.ExecuteNonQueryAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task<string> ResolveSpaceIdAsync(SqliteConnection connection, DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var bySetting = connection.CreateCommand();
        bySetting.CommandText = "SELECT value FROM settings WHERE key='active_space_id'";
        bySetting.Transaction = (SqliteTransaction)transaction;
        var settingValue = await bySetting.ExecuteScalarAsync(cancellationToken) as string;
        if (!string.IsNullOrEmpty(settingValue)) return settingValue;

        var byDefault = connection.CreateCommand();
        byDefault.CommandText = "SELECT id FROM spaces WHERE is_default=1 LIMIT 1";
        byDefault.Transaction = (SqliteTransaction)transaction;
        var defaultId = await byDefault.ExecuteScalarAsync(cancellationToken) as string;
        if (!string.IsNullOrEmpty(defaultId)) return defaultId;

        throw new InvalidDataException("未找到可用空间，迁移 017 已停止并恢复备份");
    }

    private static async Task<List<LegacyAutomation>> ReadLegacyRowsAsync(SqliteConnection connection,
        DbTransaction transaction, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = $"SELECT id,name,prompt,interval_minutes,enabled,last_run_at,next_run_at,last_status FROM {LegacyTable}";
        command.Transaction = (SqliteTransaction)transaction;
        var rows = new List<LegacyAutomation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new LegacyAutomation
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                Prompt = reader.GetString(2),
                IntervalMinutes = reader.GetInt32(3),
                Enabled = reader.GetInt32(4) == 1,
                LastRunAt = reader.IsDBNull(5) ? null : reader.GetString(5),
                NextRunAt = reader.IsDBNull(6) ? null : reader.GetString(6),
                LastStatus = reader.GetString(7)
            });
        }

        return rows;
    }

    private async Task ConvertLegacyRowAsync(SqliteConnection connection, DbTransaction transaction,
        LegacyAutomation row, string spaceId, string now, CancellationToken cancellationToken)
    {
        var revisionId = Guid.NewGuid().ToString("N");
        var intervalSeconds = Math.Clamp(row.IntervalMinutes, 1, MaxIntervalMinutes) * 60;

        var anchorNeedsReview = !TryParseAnchor(row.NextRunAt, intervalSeconds, out var anchor);
        if (anchorNeedsReview) anchor = _clock.UtcNow;
        var anchorUtc = anchor.ToString("O");
        var trigger = new JsonObject
        {
            ["trigger_id"] = DefaultTriggerId,
            ["type"] = "interval",
            ["enabled"] = true,
            ["timezone_id"] = (JsonNode?)null,
            ["interval_seconds"] = intervalSeconds,
            ["anchor_at_utc"] = anchorUtc
        };

        var workflow = new JsonObject
        {
            ["schema_version"] = 1,
            ["entry_node_id"] = AgentNodeId,
            ["nodes"] = new JsonArray(new JsonObject
            {
                ["node_id"] = AgentNodeId,
                ["kind"] = "agent_prompt",
                ["display_name"] = "指令",
                ["instruction_template"] = row.Prompt,
                ["input_bindings"] = new JsonArray(),
                ["output_key"] = "result",
                ["max_model_turns"] = 4,
                ["capability_mode"] = "declared_only"
            }),
            ["edges"] = new JsonArray()
        };

        // V1.4 的 automations 表无 project/expert 绑定字段，升级后需用户重新配置。
        var binding = new JsonObject { ["project_id"] = (JsonNode?)null, ["expert_id"] = (JsonNode?)null };
        var budget = new JsonObject
        {
            ["max_model_turns"] = 8,
            ["max_total_tokens"] = 200_000,
            ["max_duration_seconds"] = 1800,
            ["max_capability_calls"] = 20
        };

        var permissionRequest = new JsonObject
        {
            ["read_only"] = true,
            ["connector_capabilities"] = new JsonArray(),
            ["mcp_capabilities"] = new JsonArray()
        };

        var canonical = JcsCanonicalizer.Canonicalize(new JsonObject
        {
            ["trigger"] = trigger,
            ["workflow"] = workflow,
            ["binding"] = binding,
            ["budget"] = budget,
            ["overlap_policy"] = "skip",
            ["missed_run_policy"] = "skip",
            ["permission_request"] = permissionRequest
        }.ToJsonString());
        var canonicalSha = Sha256Hex(Encoding.UTF8.GetBytes(canonical));

        var lifecycle = ComputeLifecycle(row.Enabled, bindingValid: false, anchorNeedsReview);

        await InsertDefinitionAsync(connection, transaction, row, spaceId, lifecycle, now, cancellationToken);
        await InsertRevisionAsync(connection, transaction, row, revisionId, trigger, workflow, binding, budget,
            permissionRequest, canonicalSha, now, cancellationToken);
        await InsertScheduleAsync(connection, transaction, revisionId, trigger, lifecycle, now, cancellationToken);
        await LinkCurrentRevisionAsync(connection, transaction, row.Id, revisionId, cancellationToken);
        await InsertLegacyStateAsync(connection, transaction, row, now, cancellationToken);
    }

    private static bool TryParseAnchor(string? nextRunAt, int intervalSeconds, out DateTimeOffset anchor)
    {
        anchor = default;
        if (string.IsNullOrWhiteSpace(nextRunAt)) return false;
        if (!DateTimeOffset.TryParse(nextRunAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var next))
            return false;
        anchor = next - TimeSpan.FromSeconds(intervalSeconds);
        return true;
    }

    private static string ComputeLifecycle(bool enabled, bool bindingValid, bool anchorNeedsReview)
    {
        if (anchorNeedsReview || !bindingValid) return "paused_needs_review";
        return enabled ? "enabled" : "paused";
    }

    private static async Task InsertDefinitionAsync(SqliteConnection connection, DbTransaction transaction,
        LegacyAutomation row, string spaceId, string lifecycle, string now, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO automation_definitions(id,space_id,name,description,lifecycle,current_revision_id,revision_number,prior_lifecycle,created_at_utc,updated_at_utc,deleted_at_utc,row_version)
            VALUES($id,$space,$name,'',$lifecycle,NULL,1,NULL,$now,$now,NULL,1)
            """;
        command.Transaction = (SqliteTransaction)transaction;
        command.Parameters.AddWithValue("$id", row.Id);
        command.Parameters.AddWithValue("$space", spaceId);
        command.Parameters.AddWithValue("$name", row.Name);
        command.Parameters.AddWithValue("$lifecycle", lifecycle);
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertRevisionAsync(SqliteConnection connection, DbTransaction transaction,
        LegacyAutomation row, string revisionId, JsonObject trigger, JsonObject workflow, JsonObject binding,
        JsonObject budget, JsonObject permissionRequest, string canonicalSha, string now, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO automation_revisions(id,automation_id,revision_number,schema_version,trigger_json,workflow_json,binding_json,budget_json,overlap_policy,missed_run_policy,permission_request_json,canonical_sha256,created_at_utc)
            VALUES($id,$automation,1,1,$trigger,$workflow,$binding,$budget,'skip','skip',$permission,$canonical,$now)
            """;
        command.Transaction = (SqliteTransaction)transaction;
        command.Parameters.AddWithValue("$id", revisionId);
        command.Parameters.AddWithValue("$automation", row.Id);
        command.Parameters.AddWithValue("$trigger", trigger.ToJsonString());
        command.Parameters.AddWithValue("$workflow", workflow.ToJsonString());
        command.Parameters.AddWithValue("$binding", binding.ToJsonString());
        command.Parameters.AddWithValue("$budget", budget.ToJsonString());
        command.Parameters.AddWithValue("$permission", permissionRequest.ToJsonString());
        command.Parameters.AddWithValue("$canonical", canonicalSha);
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertScheduleAsync(SqliteConnection connection, DbTransaction transaction,
        string revisionId, JsonObject trigger, string lifecycle, string now, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO automation_schedules(automation_revision_id,trigger_id,trigger_kind,timezone_id,schedule_json,next_occurrence_at_utc,last_materialized_at_utc,enabled,row_version)
            VALUES($revision,$triggerId,'interval',NULL,$schedule,NULL,NULL,$enabled,1)
            """;
        command.Transaction = (SqliteTransaction)transaction;
        command.Parameters.AddWithValue("$revision", revisionId);
        command.Parameters.AddWithValue("$triggerId", DefaultTriggerId);
        command.Parameters.AddWithValue("$schedule", trigger.ToJsonString());
        command.Parameters.AddWithValue("$enabled", lifecycle == "enabled" ? 1 : 0);
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task LinkCurrentRevisionAsync(SqliteConnection connection, DbTransaction transaction,
        string automationId, string revisionId, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE automation_definitions SET current_revision_id=$revision WHERE id=$id";
        command.Transaction = (SqliteTransaction)transaction;
        command.Parameters.AddWithValue("$revision", revisionId);
        command.Parameters.AddWithValue("$id", automationId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertLegacyStateAsync(SqliteConnection connection, DbTransaction transaction,
        LegacyAutomation row, string now, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO migration_legacy_automation_state(automation_id,legacy_last_run_at_utc,legacy_next_run_at_utc,legacy_last_status,migrated_at_utc)
            VALUES($id,$lastRun,$nextRun,$status,$now)
            """;
        command.Transaction = (SqliteTransaction)transaction;
        command.Parameters.AddWithValue("$id", row.Id);
        command.Parameters.AddWithValue("$lastRun", (object?)row.LastRunAt ?? DBNull.Value);
        command.Parameters.AddWithValue("$nextRun", (object?)row.NextRunAt ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", row.LastStatus);
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task VerifyNoOrphanRevisionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var orphan = connection.CreateCommand();
        orphan.CommandText = "SELECT COUNT(*) FROM automation_definitions WHERE lifecycle<>'draft' AND current_revision_id IS NULL";
        if (Convert.ToInt32(await orphan.ExecuteScalarAsync(cancellationToken)) != 0)
            throw new InvalidDataException("迁移 017 校验失败：存在无当前修订的自动化（orphan current revision）");

        var dangling = connection.CreateCommand();
        dangling.CommandText = """
            SELECT COUNT(*) FROM automation_definitions ad
            LEFT JOIN automation_revisions ar ON ad.current_revision_id=ar.id
            WHERE ad.current_revision_id IS NOT NULL AND ar.id IS NULL
            """;
        if (Convert.ToInt32(await dangling.ExecuteScalarAsync(cancellationToken)) != 0)
            throw new InvalidDataException("迁移 017 校验失败：current_revision_id 指向不存在的修订");
    }

    private async Task<string?> GetChecksumAsync(SqliteConnection connection, int version, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT checksum FROM schema_migrations WHERE version=$version";
        command.Parameters.AddWithValue("$version", version);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async Task RecordMigrationAsync(SqliteConnection connection, DbTransaction transaction,
        int version, string name, string ddl, CancellationToken cancellationToken)
    {
        var record = connection.CreateCommand();
        record.CommandText = "INSERT INTO schema_migrations(version,name,applied_at,checksum) VALUES($version,$name,$now,$checksum)";
        record.Transaction = (SqliteTransaction)transaction;
        record.Parameters.AddWithValue("$version", version);
        record.Parameters.AddWithValue("$name", name);
        record.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        record.Parameters.AddWithValue("$checksum", Sha256(ddl));
        await record.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string CreateBackup(SqliteConnection source)
    {
        var path = source.DataSource;
        if (!File.Exists(path)) return string.Empty;
        var directory = Path.GetDirectoryName(path)!;
        var backupPath = Path.Combine(directory, $"workpilot.pre-v17.{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.db");
        using var backup = new SqliteConnection($"Data Source={backupPath}");
        backup.Open();
        source.BackupDatabase(backup);
        foreach (var old in Directory.GetFiles(directory, "workpilot.pre-v17.*.db")
                     .OrderByDescending(File.GetLastWriteTimeUtc).Skip(3))
        {
            try { File.Delete(old); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }

        return backupPath;
    }

    private static void RestoreBackup(SqliteConnection source, string backupPath)
    {
        if (source.State != System.Data.ConnectionState.Closed)
            source.Close();
        var failedPath = source.DataSource + ".failed-v17." + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
        if (File.Exists(failedPath)) File.Delete(failedPath);
        File.Move(source.DataSource, failedPath);
        using var backup = new SqliteConnection($"Data Source={backupPath};Mode=ReadOnly");
        using var destination = new SqliteConnection($"Data Source={source.DataSource}");
        backup.Open();
        destination.Open();
        backup.BackupDatabase(destination);
    }

    private static string Sha256(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed record LegacyAutomation
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Prompt { get; init; } = string.Empty;
        public int IntervalMinutes { get; init; }
        public bool Enabled { get; init; }
        public string? LastRunAt { get; init; }
        public string? NextRunAt { get; init; }
        public string LastStatus { get; init; } = string.Empty;
    }

    private const string Migration017Ddl = """
        ALTER TABLE automations RENAME TO automations_v12_legacy;

        CREATE TABLE automation_definitions(
          id TEXT PRIMARY KEY,
          space_id TEXT NOT NULL REFERENCES spaces(id) ON DELETE RESTRICT,
          name TEXT NOT NULL CHECK(length(name) BETWEEN 1 AND 80),
          description TEXT NOT NULL DEFAULT '' CHECK(length(description)<=500),
          lifecycle TEXT NOT NULL CHECK(lifecycle IN(
            'draft','enabled','paused','paused_needs_review','archived','deleted')),
          current_revision_id TEXT NULL REFERENCES automation_revisions(id) ON DELETE RESTRICT,
          revision_number INTEGER NOT NULL DEFAULT 0 CHECK(revision_number>=0),
          prior_lifecycle TEXT NULL,
          created_at_utc TEXT NOT NULL,
          updated_at_utc TEXT NOT NULL,
          deleted_at_utc TEXT NULL,
          row_version INTEGER NOT NULL DEFAULT 1
        );

        CREATE TABLE automation_revisions(
          id TEXT PRIMARY KEY,
          automation_id TEXT NOT NULL REFERENCES automation_definitions(id) ON DELETE RESTRICT,
          revision_number INTEGER NOT NULL CHECK(revision_number>=1),
          schema_version INTEGER NOT NULL CHECK(schema_version=1),
          trigger_json TEXT NOT NULL,
          workflow_json TEXT NOT NULL,
          binding_json TEXT NOT NULL,
          budget_json TEXT NOT NULL,
          overlap_policy TEXT NOT NULL CHECK(overlap_policy IN('skip','queue_one','cancel_previous')),
          missed_run_policy TEXT NOT NULL CHECK(missed_run_policy IN('skip','run_once','catch_up')),
          permission_request_json TEXT NOT NULL,
          canonical_sha256 TEXT NOT NULL CHECK(length(canonical_sha256)=64),
          created_at_utc TEXT NOT NULL,
          UNIQUE(automation_id,revision_number),
          UNIQUE(automation_id,canonical_sha256)
        );

        CREATE TABLE automation_schedules(
          automation_revision_id TEXT NOT NULL REFERENCES automation_revisions(id) ON DELETE CASCADE,
          trigger_id TEXT NOT NULL,
          trigger_kind TEXT NOT NULL CHECK(trigger_kind IN(
            'manual','once','interval','calendar_daily','calendar_weekly','calendar_monthly','domain_event')),
          timezone_id TEXT NULL,
          schedule_json TEXT NOT NULL,
          next_occurrence_at_utc TEXT NULL,
          last_materialized_at_utc TEXT NULL,
          enabled INTEGER NOT NULL CHECK(enabled IN(0,1)),
          row_version INTEGER NOT NULL DEFAULT 1,
          PRIMARY KEY(automation_revision_id,trigger_id)
        );

        CREATE TABLE domain_event_outbox(
          id TEXT PRIMARY KEY,
          event_type TEXT NOT NULL,
          space_id TEXT NOT NULL REFERENCES spaces(id) ON DELETE CASCADE,
          entity_type TEXT NOT NULL,
          entity_id TEXT NOT NULL,
          entity_version INTEGER NOT NULL,
          safe_payload_json TEXT NOT NULL,
          occurred_at_utc TEXT NOT NULL,
          dispatched_at_utc TEXT NULL,
          attempt_count INTEGER NOT NULL DEFAULT 0,
          next_attempt_at_utc TEXT NULL,
          last_error_code TEXT NULL
        );

        CREATE INDEX ix_automation_definitions_space_state
          ON automation_definitions(space_id,lifecycle,updated_at_utc DESC);
        CREATE INDEX ix_automation_schedules_due
          ON automation_schedules(enabled,next_occurrence_at_utc)
          WHERE next_occurrence_at_utc IS NOT NULL;
        CREATE INDEX ix_domain_event_outbox_pending
          ON domain_event_outbox(dispatched_at_utc,next_attempt_at_utc,occurred_at_utc);

        CREATE TABLE migration_legacy_automation_state(
          automation_id TEXT PRIMARY KEY REFERENCES automation_definitions(id) ON DELETE CASCADE,
          legacy_last_run_at_utc TEXT NULL,
          legacy_next_run_at_utc TEXT NULL,
          legacy_last_status TEXT NOT NULL,
          migrated_at_utc TEXT NOT NULL
        );
        """;

    /// <summary>
    /// Migration 018 DDL: durable run / step / event / snapshot / trigger-occurrence storage.
    /// Frozen per spec (docs 08 §3) — the SHA-256 of this string is recorded at apply time and
    /// verified on startup, so it MUST NOT drift from the published migration.
    /// </summary>
    private const string Migration018Ddl = """
        CREATE TABLE automation_trigger_occurrences(
          id TEXT PRIMARY KEY,
          automation_id TEXT NOT NULL REFERENCES automation_definitions(id),
          automation_revision_id TEXT NOT NULL REFERENCES automation_revisions(id),
          trigger_id TEXT NOT NULL,
          scheduled_at_utc TEXT NOT NULL,
          materialized_at_utc TEXT NOT NULL,
          disposition TEXT NOT NULL CHECK(disposition IN('queued','skipped_missed','skipped_overlap','coalesced','blocked')),
          dedupe_key TEXT NOT NULL UNIQUE CHECK(length(dedupe_key)=64),
          missed_count INTEGER NOT NULL DEFAULT 0,
          safe_trigger_json TEXT NOT NULL
        );

        CREATE TABLE automation_run_snapshots(
          id TEXT PRIMARY KEY,
          automation_revision_id TEXT NOT NULL REFERENCES automation_revisions(id),
          expert_revision_id TEXT NOT NULL REFERENCES expert_revisions(id),
          policy_snapshot_json TEXT NOT NULL,
          capability_snapshot_json TEXT NOT NULL,
          workflow_snapshot_json TEXT NOT NULL,
          binding_snapshot_json TEXT NOT NULL,
          budget_snapshot_json TEXT NOT NULL,
          revocation_epoch INTEGER NOT NULL,
          algorithm_versions_json TEXT NOT NULL,
          canonical_sha256 TEXT NOT NULL CHECK(length(canonical_sha256)=64),
          created_at_utc TEXT NOT NULL
        );

        CREATE TABLE automation_runs(
          id TEXT PRIMARY KEY,
          automation_id TEXT NULL REFERENCES automation_definitions(id) ON DELETE SET NULL,
          automation_revision_id TEXT NOT NULL REFERENCES automation_revisions(id),
          occurrence_id TEXT NULL UNIQUE REFERENCES automation_trigger_occurrences(id),
          snapshot_id TEXT NOT NULL UNIQUE REFERENCES automation_run_snapshots(id),
          parent_run_id TEXT NULL REFERENCES automation_runs(id),
          trigger_kind TEXT NOT NULL,
          status TEXT NOT NULL CHECK(status IN(
            'queued','claimed','running','waiting_delay','waiting_approval',
            'completed','failed','cancelled','blocked_policy','needs_review')),
          priority INTEGER NOT NULL DEFAULT 0 CHECK(priority BETWEEN -10 AND 10),
          scheduled_at_utc TEXT NOT NULL,
          available_at_utc TEXT NOT NULL,
          claimed_at_utc TEXT NULL,
          started_at_utc TEXT NULL,
          finished_at_utc TEXT NULL,
          lease_owner TEXT NULL,
          lease_expires_at_utc TEXT NULL,
          cancellation_requested_at_utc TEXT NULL,
          current_node_id TEXT NULL,
          last_event_sequence INTEGER NOT NULL DEFAULT 0,
          active_duration_ms INTEGER NOT NULL DEFAULT 0,
          model_turn_count INTEGER NOT NULL DEFAULT 0,
          capability_call_count INTEGER NOT NULL DEFAULT 0,
          result_bytes INTEGER NOT NULL DEFAULT 0,
          coalesced_count INTEGER NOT NULL DEFAULT 0,
          recovery_count INTEGER NOT NULL DEFAULT 0,
          final_error_code TEXT NULL,
          row_version INTEGER NOT NULL DEFAULT 1
        );

        CREATE TABLE automation_step_runs(
          id TEXT PRIMARY KEY,
          run_id TEXT NOT NULL REFERENCES automation_runs(id) ON DELETE CASCADE,
          node_id TEXT NOT NULL,
          logical_execution INTEGER NOT NULL DEFAULT 1,
          attempt INTEGER NOT NULL DEFAULT 1,
          node_kind TEXT NOT NULL,
          status TEXT NOT NULL CHECK(status IN(
            'pending','ready','running','waiting_delay','waiting_approval','succeeded',
            'skipped','failed','cancelled','outcome_unknown','blocked_policy')),
          side_effect_phase TEXT NULL CHECK(side_effect_phase IS NULL OR side_effect_phase IN(
            'prepared','permit_issued','request_sending','response_received','persisted')),
          idempotency_key TEXT NOT NULL,
          input_digest TEXT NOT NULL,
          output_summary_json TEXT NULL,
          resume_at_utc TEXT NULL,
          started_at_utc TEXT NULL,
          finished_at_utc TEXT NULL,
          duration_ms INTEGER NOT NULL DEFAULT 0,
          error_code TEXT NULL,
          row_version INTEGER NOT NULL DEFAULT 1,
          UNIQUE(run_id,node_id,logical_execution,attempt)
        );

        CREATE TABLE run_events(
          id TEXT PRIMARY KEY,
          run_id TEXT NOT NULL REFERENCES automation_runs(id) ON DELETE CASCADE,
          sequence INTEGER NOT NULL,
          occurred_at_utc TEXT NOT NULL,
          kind TEXT NOT NULL,
          level TEXT NOT NULL CHECK(level IN('trace','info','warning','error','security')),
          step_id TEXT NULL REFERENCES automation_step_runs(id) ON DELETE SET NULL,
          attempt INTEGER NULL,
          code TEXT NOT NULL,
          message_key TEXT NOT NULL,
          safe_properties_json TEXT NOT NULL,
          correlation_id TEXT NOT NULL,
          UNIQUE(run_id,sequence)
        );

        CREATE TABLE approval_requests(
          id TEXT PRIMARY KEY,
          run_id TEXT NOT NULL REFERENCES automation_runs(id) ON DELETE CASCADE,
          step_id TEXT NOT NULL REFERENCES automation_step_runs(id) ON DELETE CASCADE,
          status TEXT NOT NULL CHECK(status IN('pending','approved','denied','expired','invalidated')),
          source_kind TEXT NOT NULL,
          source_id TEXT NOT NULL,
          capability_stable_id TEXT NOT NULL,
          schema_sha256 TEXT NOT NULL,
          argument_digest TEXT NOT NULL,
          scope_digest TEXT NOT NULL,
          safe_summary_json TEXT NOT NULL,
          risk_level INTEGER NOT NULL CHECK(risk_level BETWEEN 0 AND 3),
          policy_trace_sha256 TEXT NOT NULL,
          expires_at_utc TEXT NOT NULL,
          decided_at_utc TEXT NULL,
          decision_reason TEXT NULL,
          created_at_utc TEXT NOT NULL,
          row_version INTEGER NOT NULL DEFAULT 1
        );

        CREATE INDEX ix_occurrences_automation_time ON automation_trigger_occurrences(automation_id,scheduled_at_utc DESC);
        CREATE INDEX ix_runs_queue ON automation_runs(status,available_at_utc,priority,scheduled_at_utc);
        CREATE INDEX ix_runs_history ON automation_runs(started_at_utc DESC,id DESC);
        CREATE INDEX ix_runs_automation ON automation_runs(automation_id,status,scheduled_at_utc DESC);
        CREATE INDEX ix_runs_lease ON automation_runs(status,lease_expires_at_utc) WHERE lease_expires_at_utc IS NOT NULL;
        CREATE INDEX ix_steps_run ON automation_step_runs(run_id,node_id,attempt);
        CREATE INDEX ix_events_run_sequence ON run_events(run_id,sequence);
        CREATE INDEX ix_approvals_pending ON approval_requests(status,expires_at_utc);

        CREATE UNIQUE INDEX ux_approval_one_pending_step
          ON approval_requests(run_id,step_id) WHERE status='pending';
        """;

    /// <summary>
    /// Migration 019 DDL: policy governance storage (doc 07 §3/§13, PER-001/009/010, SEC-106).
    /// Frozen per spec — the SHA-256 of this string is recorded at apply time and verified on
    /// startup, so it MUST NOT drift from the published migration. Default policy DATA is not
    /// seeded here (see PolicyBootstrapper / EnsureDefaultPolicyAsync).
    /// </summary>
    private const string Migration019Ddl = """
        CREATE TABLE policy_documents(
          id TEXT PRIMARY KEY,
          layer TEXT NOT NULL CHECK(layer IN(
            'BuiltInSafety','GlobalPolicy','SpacePolicy','ExpertPolicy','AutomationPolicy')),
          scope_id TEXT NULL,
          current_version_id TEXT NULL,
          created_at_utc TEXT NOT NULL,
          updated_at_utc TEXT NOT NULL,
          row_version INTEGER NOT NULL DEFAULT 1,
          UNIQUE(layer,scope_id)
        );

        CREATE TABLE policy_versions(
          id TEXT PRIMARY KEY,
          document_id TEXT NOT NULL REFERENCES policy_documents(id) ON DELETE CASCADE,
          version_number INTEGER NOT NULL CHECK(version_number>=1),
          canonical_sha256 TEXT NOT NULL CHECK(length(canonical_sha256)=64),
          document_json TEXT NOT NULL,
          is_default INTEGER NOT NULL CHECK(is_default IN(0,1)),
          created_at_utc TEXT NOT NULL,
          UNIQUE(document_id,version_number)
        );

        CREATE TABLE policy_statements(
          id TEXT PRIMARY KEY,
          version_id TEXT NOT NULL REFERENCES policy_versions(id) ON DELETE CASCADE,
          enabled INTEGER NOT NULL CHECK(enabled IN(0,1)),
          effect TEXT NOT NULL CHECK(effect IN('Allow','Ask','Deny')),
          subjects TEXT NOT NULL,
          source_selector_json TEXT NOT NULL,
          capability_selector_json TEXT NOT NULL,
          risk_min INTEGER NOT NULL CHECK(risk_min BETWEEN 0 AND 3),
          risk_max INTEGER NOT NULL CHECK(risk_max BETWEEN 0 AND 3),
          resource_scope_json TEXT NULL,
          conditions_json TEXT NOT NULL,
          priority INTEGER NOT NULL,
          created_at_utc TEXT NOT NULL
        );

        CREATE TABLE policy_grants(
          grant_id TEXT PRIMARY KEY,
          automation_id TEXT NOT NULL,
          revision_id TEXT NOT NULL,
          space_id TEXT NULL,
          expert_revision_id TEXT NULL,
          source_kind TEXT NOT NULL,
          source_id TEXT NOT NULL,
          capability_stable_id TEXT NOT NULL,
          schema_sha256 TEXT NOT NULL,
          resource_scope_json TEXT NOT NULL,
          scope_sha256 TEXT NOT NULL,
          risk_ceiling INTEGER NOT NULL CHECK(risk_ceiling BETWEEN 0 AND 3),
          not_before_utc TEXT NOT NULL,
          expires_at_utc TEXT NOT NULL,
          revocation_epoch_at_issue INTEGER NOT NULL,
          created_at_utc TEXT NOT NULL,
          revoked_at_utc TEXT NULL
        );

        CREATE TABLE consent_receipts(
          receipt_id TEXT PRIMARY KEY,
          run_id TEXT NOT NULL,
          step_id TEXT NOT NULL,
          attempt INTEGER NOT NULL,
          source_kind TEXT NOT NULL,
          source_id TEXT NOT NULL,
          capability_stable_id TEXT NOT NULL,
          schema_sha256 TEXT NOT NULL,
          argument_digest TEXT NOT NULL,
          scope_digest TEXT NOT NULL,
          risk_level INTEGER NOT NULL CHECK(risk_level BETWEEN 0 AND 3),
          policy_hash TEXT NOT NULL,
          epoch INTEGER NOT NULL,
          issued_at_utc TEXT NOT NULL,
          expires_at_utc TEXT NOT NULL,
          status TEXT NOT NULL CHECK(status IN('issued','consumed','invalidated','expired')),
          consumed_at_utc TEXT NULL
        );

        CREATE TABLE policy_audit(
          id TEXT PRIMARY KEY,
          occurred_at_utc TEXT NOT NULL,
          layer TEXT NULL CHECK(layer IS NULL OR layer IN(
            'BuiltInSafety','GlobalPolicy','SpacePolicy','ExpertPolicy','AutomationPolicy')),
          action TEXT NOT NULL CHECK(action IN(
            'bootstrap','recovery','user_save','grant_issued','grant_revoked','receipt_consumed',
            'receipt_invalidated','legacy_v14','integrity_check')),
          document_id TEXT NULL,
          version_id TEXT NULL,
          reason_code TEXT NULL,
          actor TEXT NULL,
          source TEXT NOT NULL CHECK(source IN(
            'bootstrap','recovery','user_save','grant','receipt','legacy_v14')),
          detail_json TEXT NOT NULL,
          policy_hash TEXT NULL,
          created_at_utc TEXT NOT NULL
        );

        CREATE INDEX ix_policy_versions_document ON policy_versions(document_id,version_number DESC);
        CREATE INDEX ix_policy_statements_version ON policy_statements(version_id,enabled);
        CREATE INDEX ix_policy_grants_automation ON policy_grants(automation_id,revision_id);
        CREATE INDEX ix_consent_receipts_policy ON consent_receipts(policy_hash,status);
        CREATE INDEX ix_policy_audit_time ON policy_audit(occurred_at_utc DESC,source);
        CREATE INDEX ix_policy_audit_document ON policy_audit(document_id,occurred_at_utc DESC);
        """;

    /// <summary>
    /// Migration 020 DDL: security storage (doc 06 §2/§3/§8, SEC-102/103/106). Four append-friendly,
    /// display-name-free tables: <c>security_events</c>, <c>incidents</c>, <c>security_audit_log</c>,
    /// <c>detector_actions</c>. The audit log is protected by an HMAC chain computed by the
    /// application layer (not the DDL). No cross-migration foreign keys exist, so the schema is safe
    /// to create standalone. Frozen per spec — the SHA-256 of this string is recorded at apply time
    /// and verified on startup, so it MUST NOT drift from the published migration.
    /// </summary>
    private const string Migration020Ddl = """
        CREATE TABLE security_events(
          id TEXT PRIMARY KEY,
          occurred_at_utc TEXT NOT NULL,
          type INTEGER NOT NULL CHECK(type BETWEEN 0 AND 15),
          severity INTEGER NOT NULL CHECK(severity BETWEEN 0 AND 4),
          fingerprint TEXT NOT NULL CHECK(length(fingerprint)=64),
          source_kind TEXT NULL,
          source_id TEXT NULL,
          automation_id TEXT NULL,
          run_id TEXT NULL,
          safe_evidence_json TEXT NOT NULL,
          detector_version TEXT NOT NULL,
          created_at_utc TEXT NOT NULL
        );

        CREATE TABLE incidents(
          id TEXT PRIMARY KEY,
          fingerprint TEXT NOT NULL CHECK(length(fingerprint)=64),
          state INTEGER NOT NULL CHECK(state BETWEEN 0 AND 4),
          severity INTEGER NOT NULL CHECK(severity BETWEEN 0 AND 4),
          type INTEGER NOT NULL CHECK(type BETWEEN 0 AND 15),
          first_seen_utc TEXT NOT NULL,
          last_seen_utc TEXT NOT NULL,
          count INTEGER NOT NULL CHECK(count>=1),
          recent_evidence_digests_json TEXT NOT NULL,
          resolution_code TEXT NULL,
          resolution_note TEXT NULL CHECK(length(resolution_note)<=500),
          resolved_at_utc TEXT NULL,
          created_at_utc TEXT NOT NULL,
          updated_at_utc TEXT NOT NULL,
          last_action_id TEXT NULL
        );

        CREATE TABLE security_audit_log(
          sequence INTEGER PRIMARY KEY,
          occurred_at_utc TEXT NOT NULL,
          category INTEGER NOT NULL CHECK(category BETWEEN 0 AND 4),
          action TEXT NOT NULL,
          actor TEXT NOT NULL,
          subject_json TEXT NOT NULL,
          decision_trace_json TEXT NOT NULL,
          safe_detail_json TEXT NOT NULL,
          prev_hmac TEXT NOT NULL CHECK(length(prev_hmac)=64 OR prev_hmac='0'),
          hmac TEXT NOT NULL CHECK(length(hmac)=64),
          created_at_utc TEXT NOT NULL
        );

        CREATE TABLE detector_actions(
          action_id TEXT PRIMARY KEY,
          applied_at_utc TEXT NOT NULL
        );

        CREATE INDEX ix_security_events_fingerprint_time ON security_events(fingerprint,occurred_at_utc DESC);
        CREATE INDEX ix_security_events_time ON security_events(occurred_at_utc DESC);
        CREATE INDEX ix_incidents_fingerprint ON incidents(fingerprint,last_seen_utc DESC);
        CREATE INDEX ix_incidents_state_time ON incidents(state,last_seen_utc DESC);
        CREATE INDEX ix_security_audit_log_sequence ON security_audit_log(sequence);
        """;

    /// <summary>
    /// Migration 021 DDL: security governance state (doc 06 §6.4, SEC-101). A display-name-free
    /// key/value store for governance flags (currently <c>emergency_stop</c>) backing
    /// <c>ISecurityStateStore</c>, plus the single-row <c>revocation_epoch</c> backing the
    /// process-wide <c>IRevocationEpoch</c>. Neither holds secrets. Frozen per spec — the SHA-256 of
    /// this string is recorded at apply time and verified on startup, so it MUST NOT drift.
    /// </summary>
    private const string Migration021Ddl = """
        CREATE TABLE security_state(
          key TEXT PRIMARY KEY,
          value TEXT NOT NULL
        );

        CREATE TABLE revocation_epoch(
          epoch INTEGER NOT NULL
        );

        CREATE INDEX ix_security_state_key ON security_state(key);
        """;

    /// <summary>
    /// Migration 022 DDL: retention settings singleton (doc 05 §9, SEC-106). One row (singleton_id=1)
    /// holding the run / event / audit retention windows plus the last-successful-cleanup timestamp.
    /// The CHECK bounds mirror <see cref="RetentionPolicy.Clamp"/> so a corrupt value can never be
    /// persisted. Frozen per spec — the SHA-256 of this string is recorded at apply time and verified
    /// on startup, so it MUST NOT drift.
    /// </summary>
    private const string Migration022Ddl = """
        CREATE TABLE retention_settings(
          singleton_id INTEGER PRIMARY KEY CHECK(singleton_id=1),
          run_days INTEGER NOT NULL CHECK(run_days BETWEEN 30 AND 365),
          event_days INTEGER NOT NULL CHECK(event_days BETWEEN 7 AND 90),
          audit_days INTEGER NOT NULL CHECK(audit_days BETWEEN 90 AND 730),
          last_cleanup_at_utc TEXT NULL,
          updated_at_utc TEXT NOT NULL,
          row_version INTEGER NOT NULL
        );
        """;
}
