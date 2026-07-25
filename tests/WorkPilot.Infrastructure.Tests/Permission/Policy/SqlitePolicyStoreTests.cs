using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.PermissionGovernance;
using WorkPilot.Infrastructure.Data;
using WorkPilot.Infrastructure.Permission.Policy;
using Xunit;

namespace WorkPilot.Infrastructure.Tests.Permission.Policy;

/// <summary>T16: policy store lifecycle — minimum defaults, immutable versions, recovery, receipt invalidation, integrity.</summary>
public sealed class SqlitePolicyStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly IClock Clock = new FakeClock(Now);

    private static async Task<(SqliteConnection Conn, SqlitePolicyStore Store)> NewStoreAsync()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        var migrator = new V15DatabaseMigrator(Clock);
        await migrator.CreatePolicyTablesAsync(conn, TestContext.Current);
        var store = new SqlitePolicyStore(conn, new SequentialIdGenerator());
        return (conn, store);
    }

    private static PolicyStatement AllowStatement(IIdGenerator ids, string capability)
        => PolicyStatement.Create(ids, PolicyVersionId.Parse("tmp"), true, PolicyEffect.Allow,
            new[] { PolicySubject.AutomationPrincipal },
            "{\"source\":\"connector:github\"}",
            $"{{\"capability\":\"{capability}\"}}",
            RiskLevel.Low, RiskLevel.Low, null, Array.Empty<PolicyCondition>(), 0);

    [Fact]
    public async Task EnsureDefault_seeds_minimum_permission_baseline()
    {
        var (conn, store) = await NewStoreAsync();
        try
        {
            await store.EnsureDefaultPolicyAsync(Clock, TestContext.Current);

            var builtin = (await store.GetCurrentAsync(PolicyLayer.BuiltInSafety, null, TestContext.Current)).Value!;
            Assert.Single(builtin.Statements);
            Assert.Equal(PolicyEffect.Deny, builtin.Statements[0].Effect);
            Assert.Equal(RiskLevel.Critical, builtin.Statements[0].RiskMax);

            var global = (await store.GetCurrentAsync(PolicyLayer.GlobalPolicy, null, TestContext.Current)).Value!;
            Assert.Empty(global.Statements); // fail-closed: nothing allowed by default
            Assert.All(global.Statements, s => Assert.NotEqual(PolicyEffect.Allow, s.Effect));

            // No wildcard Allow anywhere in the default baseline.
            foreach (var layer in new[] { PolicyLayer.BuiltInSafety, PolicyLayer.GlobalPolicy, PolicyLayer.SpacePolicy, PolicyLayer.ExpertPolicy, PolicyLayer.AutomationPolicy })
            {
                var r = await store.GetCurrentAsync(layer, null, TestContext.Current);
                if (r.IsSuccess)
                    Assert.All(r.Value!.Statements, s => Assert.False(s.HasWildcardAllow()));
            }
        }
        finally
        {
            conn.Close();
        }
    }

    [Fact]
    public async Task SaveNewVersion_creates_immutable_new_version()
    {
        var (conn, store) = await NewStoreAsync();
        try
        {
            await store.RecoverDefaultAsync(PolicyLayer.GlobalPolicy, null, Clock, TestContext.Current);
            var current = (await store.GetCurrentAsync(PolicyLayer.GlobalPolicy, null, TestContext.Current)).Value!;
            var docId = current.Document.Id;
            var v1Hash = current.Version.CanonicalSha256;
            Assert.Empty(current.Statements); // v1 default empty

            var ids = new SequentialIdGenerator();
            var save = await store.SaveNewVersionAsync(docId,
                new[] { AllowStatement(ids, "issue.create") }, "alice", "explicit_allow", Clock, TestContext.Current);
            Assert.True(save.IsSuccess);

            // Current is now v2; v1 still exists and is unchanged.
            var after = (await store.GetCurrentAsync(PolicyLayer.GlobalPolicy, null, TestContext.Current)).Value!;
            Assert.Equal(2, after.Version.VersionNumber);
            Assert.Single(after.Statements);
            Assert.Equal(PolicyEffect.Allow, after.Statements[0].Effect);

            var versionCount = await CountAsync(conn, "SELECT COUNT(*) FROM policy_versions WHERE document_id=$d", docId.Value);
            Assert.Equal(2, versionCount); // v1 + v2 both present (immutable)

            var v1Statements = await CountAsync(conn, "SELECT COUNT(*) FROM policy_statements ps JOIN policy_versions pv ON ps.version_id=pv.id WHERE pv.document_id=$d AND pv.version_number=1", docId.Value);
            Assert.Equal(0, v1Statements); // v1 still empty
            Assert.NotEqual(v1Hash, after.Version.CanonicalSha256);
        }
        finally
        {
            conn.Close();
        }
    }

    [Fact]
    public async Task RecoverDefault_preserves_historical_audit()
    {
        var (conn, store) = await NewStoreAsync();
        try
        {
            await store.EnsureDefaultPolicyAsync(Clock, TestContext.Current); // BuiltIn + Global defaults → 2 audit rows
            var before = await CountAsync(conn, "SELECT COUNT(*) FROM policy_audit");
            Assert.Equal(2, before);

            // A user save changes the policy hash, appending a user_save audit AND a
            // receipt_invalidated audit (no receipts exist yet, but the rule still records it).
            var global = (await store.GetCurrentAsync(PolicyLayer.GlobalPolicy, null, TestContext.Current)).Value!;
            var ids = new SequentialIdGenerator();
            await store.SaveNewVersionAsync(global.Document.Id, new[] { AllowStatement(ids, "issue.create") }, "alice", "explicit_allow", Clock, TestContext.Current);
            var afterSave = await CountAsync(conn, "SELECT COUNT(*) FROM policy_audit");
            Assert.Equal(4, afterSave);

            // Recovery appends a NEW version and audit row; it must NOT delete prior audit (PER-010).
            await store.RecoverDefaultAsync(PolicyLayer.GlobalPolicy, null, Clock, TestContext.Current);
            var afterRecovery = await CountAsync(conn, "SELECT COUNT(*) FROM policy_audit");
            Assert.True(afterRecovery > before, "recovery must preserve and grow audit, never delete");

            // The original user_save audit row is still present.
            var hasUserSave = await ExistsAsync(conn, "SELECT 1 FROM policy_audit WHERE action='user_save' AND source='user_save'");
            Assert.True(hasUserSave);
        }
        finally
        {
            conn.Close();
        }
    }

    [Fact]
    public async Task Policy_change_invalidates_receipts_bound_to_old_hash()
    {
        var (conn, store) = await NewStoreAsync();
        try
        {
            await store.RecoverDefaultAsync(PolicyLayer.GlobalPolicy, null, Clock, TestContext.Current);
            var current = (await store.GetCurrentAsync(PolicyLayer.GlobalPolicy, null, TestContext.Current)).Value!;
            var oldHash = current.Version.CanonicalSha256;

            // A consent receipt was issued against the old policy hash.
            await InsertReceiptAsync(conn, "rcpt_1", oldHash);
            Assert.Equal("issued", await ReceiptStatusAsync(conn, "rcpt_1"));

            var ids = new SequentialIdGenerator();
            await store.SaveNewVersionAsync(current.Document.Id, new[] { AllowStatement(ids, "issue.create") }, "alice", "explicit_allow", Clock, TestContext.Current);

            // The old receipt must now be invalidated (旧 receipt invalid).
            Assert.Equal("invalidated", await ReceiptStatusAsync(conn, "rcpt_1"));
        }
        finally
        {
            conn.Close();
        }
    }

    [Fact]
    public async Task VerifyIntegrity_detects_tampered_statements()
    {
        var (conn, store) = await NewStoreAsync();
        try
        {
            // BuiltInSafety default carries one Deny statement, so tampering is observable.
            await store.RecoverDefaultAsync(PolicyLayer.BuiltInSafety, null, Clock, TestContext.Current);
            var integrity = await store.VerifyIntegrityAsync(TestContext.Current);
            Assert.True(integrity.Value);

            // Tamper with a stored statement (simulating on-disk corruption).
            await using (var tx = await conn.BeginTransactionAsync(TestContext.Current))
            {
                var tamper = conn.CreateCommand();
                tamper.CommandText = "UPDATE policy_statements SET effect='Allow' WHERE effect='Deny'";
                tamper.Transaction = (SqliteTransaction)tx;
                await tamper.ExecuteNonQueryAsync(TestContext.Current);
                await tx.CommitAsync(TestContext.Current);
            }

            var after = await store.VerifyIntegrityAsync(TestContext.Current);
            Assert.False(after.Value);
        }
        finally
        {
            conn.Close();
        }
    }

    private static async Task<int> CountAsync(SqliteConnection conn, string sql, string? param = null)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (param is not null)
            cmd.Parameters.AddWithValue("$d", param);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(TestContext.Current));
    }

    private static async Task<bool> ExistsAsync(SqliteConnection conn, string sql)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync(TestContext.Current) is not null;
    }

    private static async Task InsertReceiptAsync(SqliteConnection conn, string receiptId, string policyHash)
    {
        await using var tx = await conn.BeginTransactionAsync(TestContext.Current);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO consent_receipts(receipt_id,run_id,step_id,attempt,source_kind,source_id,capability_stable_id,schema_sha256,argument_digest,scope_digest,risk_level,policy_hash,epoch,issued_at_utc,expires_at_utc,status,consumed_at_utc)
            VALUES($id,'run_1','step_1',1,'connector','github','issue.create','sha', 'ad','sd',1,$hash,1,$now,$exp,'issued',NULL)
            """;
        cmd.Parameters.AddWithValue("$id", receiptId);
        cmd.Parameters.AddWithValue("$hash", policyHash);
        cmd.Parameters.AddWithValue("$now", Now.ToString("O"));
        cmd.Parameters.AddWithValue("$exp", Now.AddMinutes(5).ToString("O"));
        cmd.Transaction = (SqliteTransaction)tx;
        await cmd.ExecuteNonQueryAsync(TestContext.Current);
        await tx.CommitAsync(TestContext.Current);
    }

    private static async Task<string> ReceiptStatusAsync(SqliteConnection conn, string receiptId)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT status FROM consent_receipts WHERE receipt_id=$id";
        cmd.Parameters.AddWithValue("$id", receiptId);
        return (string)(await cmd.ExecuteScalarAsync(TestContext.Current))!;
    }
}
