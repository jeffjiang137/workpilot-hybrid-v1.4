using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using WorkPilot.Application.Permission.Policy;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.PermissionGovernance;

namespace WorkPilot.Infrastructure.Permission.Policy;

/// <summary>
/// SQLite implementation of <see cref="IPolicyStore"/> (T16). All version writes are INSERT-only and
/// the current pointer is CAS-updated, so versions are immutable (PER-009). Recovery appends a new
/// default version and invalidates receipts bound to the superseded policy hash without deleting
/// audit (PER-010). Audit records are append-only and verifiable (SEC-106).
/// </summary>
public sealed class SqlitePolicyStore : IPolicyStore, IGrantStore
{
    private readonly SqliteConnection _connection;
    private readonly IIdGenerator _ids;

    public SqlitePolicyStore(SqliteConnection connection, IIdGenerator ids)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
    }

    public async Task<Result<PolicyVersionId>> SaveNewVersionAsync(
        PolicyDocumentId documentId,
        IReadOnlyList<PolicyStatement> statements,
        string actor,
        string reasonCode,
        IClock clock,
        CancellationToken ct = default)
    {
        var doc = await ReadDocumentAsync(documentId, ct);
        if (doc is null)
            return Result<PolicyVersionId>.Fail(PolicyErrors.NotFoundError());

        var oldHash = doc.CurrentVersionId is null ? null : await ReadVersionHashAsync(doc.CurrentVersionId.Value, ct);
        var newVersionId = PolicyVersionId.Create(_ids);
        var nextNumber = await NextVersionNumberAsync(documentId, ct);
        var now = clock.UtcNow.ToString("O");

        await using var tx = await _connection.BeginTransactionAsync(ct);
        try
        {
            var rebuilt = RebindStatements(statements, newVersionId);
            var (canonical, hash) = Canonicalize(rebuilt);
            await InsertVersionAsync(tx, newVersionId, documentId, nextNumber, hash, canonical, isDefault: false, now, ct);
            await InsertStatementsAsync(tx, newVersionId, rebuilt, now, ct);
            await UpdateCurrentVersionAsync(tx, documentId, newVersionId, now, ct);
            await WriteAuditAsync(tx, clock, doc.Layer, PolicyAuditAction.UserSave, PolicyAuditSource.UserSave,
                documentId, newVersionId, reasonCode, actor, hash, $"{{\"version\":{nextNumber}}}", ct);
            if (oldHash is not null && !string.Equals(oldHash, hash, StringComparison.Ordinal))
                await InvalidateReceiptsCoreAsync(tx, oldHash, now, ct);
            await tx.CommitAsync(ct);
            return Result<PolicyVersionId>.Ok(newVersionId);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<Result<PolicyVersionId>> RecoverDefaultAsync(
        PolicyLayer layer,
        string? scopeId,
        IClock clock,
        CancellationToken ct = default)
    {
        var existing = await ReadDocumentByLayerScopeAsync(layer, scopeId, ct);
        string docId;
        int nextNumber;
        string? oldHash;
        if (existing is null)
        {
            docId = PolicyDocumentId.Create(_ids).Value;
            nextNumber = 1;
            oldHash = null;
            var now0 = clock.UtcNow.ToString("O");
            await using (var tx0 = await _connection.BeginTransactionAsync(ct))
            {
                await InsertDocumentAsync(tx0, docId, layer, scopeId, now0, ct);
                await tx0.CommitAsync(ct);
            }
        }
        else
        {
            docId = existing.Id.Value;
            nextNumber = await NextVersionNumberAsync(existing.Id, ct);
            oldHash = existing.CurrentVersionId is null ? null : await ReadVersionHashAsync(existing.CurrentVersionId.Value, ct);
        }

        var newVersionId = PolicyVersionId.Create(_ids);
        var statements = DefaultPolicyProvider.GetDefaultStatements(layer, _ids, newVersionId);
        var (canonical, hash) = Canonicalize(statements);
        var now = clock.UtcNow.ToString("O");

        await using var tx = await _connection.BeginTransactionAsync(ct);
        try
        {
            await InsertVersionAsync(tx, newVersionId, PolicyDocumentId.Parse(docId), nextNumber, hash, canonical, isDefault: true, now, ct);
            await InsertStatementsAsync(tx, newVersionId, statements, now, ct);
            await UpdateCurrentVersionAsync(tx, PolicyDocumentId.Parse(docId), newVersionId, now, ct);
            await WriteAuditAsync(tx, clock, layer, PolicyAuditAction.Recovery, PolicyAuditSource.Recovery,
                PolicyDocumentId.Parse(docId), newVersionId, "default_policy_recovery", "system", hash,
                $"{{\"layer\":\"{layer}\",\"version\":{nextNumber}}}", ct);
            if (oldHash is not null && !string.Equals(oldHash, hash, StringComparison.Ordinal))
                await InvalidateReceiptsCoreAsync(tx, oldHash, now, ct);
            await tx.CommitAsync(ct);
            return Result<PolicyVersionId>.Ok(newVersionId);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<Result<CurrentPolicyBundle>> GetCurrentAsync(
        PolicyLayer layer,
        string? scopeId,
        CancellationToken ct = default)
    {
        var doc = await ReadDocumentByLayerScopeAsync(layer, scopeId, ct);
        if (doc is null)
            return Result<CurrentPolicyBundle>.Fail(PolicyErrors.NotFoundError());
        if (doc.CurrentVersionId is null)
            return Result<CurrentPolicyBundle>.Fail(PolicyErrors.VersionNotFoundError(doc.CurrentVersionId!.Value));
        var version = await ReadVersionAsync(doc.CurrentVersionId.Value, ct);
        var statements = await ReadStatementsAsync(doc.CurrentVersionId.Value, ct);
        return Result<CurrentPolicyBundle>.Ok(new CurrentPolicyBundle(doc, version!, statements));
    }

    public async Task<Result<PolicyAuditPage>> ListAuditAsync(int limit, int offset, CancellationToken ct = default)
    {
        var totalCmd = _connection.CreateCommand();
        totalCmd.CommandText = "SELECT COUNT(*) FROM policy_audit";
        var total = Convert.ToInt32(await totalCmd.ExecuteScalarAsync(ct));

        var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id,occurred_at_utc,layer,action,document_id,version_id,reason_code,actor,source,detail_json,policy_hash,created_at_utc FROM policy_audit ORDER BY occurred_at_utc DESC, created_at_utc DESC LIMIT $limit OFFSET $offset";
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);
        var items = new List<PolicyAuditRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            items.Add(ReadAudit(reader));
        return Result<PolicyAuditPage>.Ok(new PolicyAuditPage(items, total));
    }

    public async Task<Result<bool>> VerifyIntegrityAsync(CancellationToken ct = default)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id FROM policy_versions";
        var ids = new List<string>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                ids.Add(reader.GetString(0));

        foreach (var versionId in ids)
        {
            var stored = await ReadVersionHashAsync(PolicyVersionId.Parse(versionId), ct);
            var statements = await ReadStatementsAsync(PolicyVersionId.Parse(versionId), ct);
            var computed = PolicyCanonicalizer.HashStatements(statements);
            if (!string.Equals(stored, computed, StringComparison.Ordinal))
                return Result<bool>.Ok(false);
        }

        return Result<bool>.Ok(true);
    }

    public async Task InvalidateReceiptsForPolicyHashAsync(string policyHash, IClock clock, CancellationToken ct = default)
    {
        await using var tx = await _connection.BeginTransactionAsync(ct);
        try
        {
            await InvalidateReceiptsCoreAsync(tx, policyHash, clock.UtcNow.ToString("O"), ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task EnsureDefaultPolicyAsync(IClock clock, CancellationToken ct = default)
    {
        foreach (var layer in new[] { PolicyLayer.BuiltInSafety, PolicyLayer.GlobalPolicy })
        {
            var existing = await ReadDocumentByLayerScopeAsync(layer, null, ct);
            if (existing is null)
                await RecoverDefaultAsync(layer, null, clock, ct);
        }
    }

    // ---- IGrantStore (PER-004) ----

    public async Task<Result<PolicyGrantId>> IssueAsync(PolicyGrant grant, IClock clock, CancellationToken ct = default)
    {
        var now = clock.UtcNow.ToString("O");
        await using var tx = await _connection.BeginTransactionAsync(ct);
        try
        {
            await InsertGrantAsync(tx, grant, now, ct);
            await WriteGrantAuditAsync(tx, clock, PolicyAuditAction.GrantIssued,
                $"{{\"grant_id\":\"{grant.GrantId.Value}\",\"automation_id\":\"{grant.AutomationId}\",\"revision_id\":\"{grant.RevisionId}\",\"capability\":\"{grant.CapabilityStableId}\",\"risk_ceiling\":\"{grant.RiskCeiling}\",\"expires_at_utc\":\"{grant.ExpiresAtUtc:O}\"}}",
                ct);
            await tx.CommitAsync(ct);
            return Result<PolicyGrantId>.Ok(grant.GrantId);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<Result<PolicyGrant>> GetAsync(PolicyGrantId id, CancellationToken ct = default)
    {
        var g = await ReadGrantByIdAsync(id, ct);
        return g is null
            ? Result<PolicyGrant>.Fail(PolicyErrors.GrantNotFoundError(id.Value))
            : Result<PolicyGrant>.Ok(g);
    }

    public async Task<Result<IReadOnlyList<PolicyGrant>>> ListByAutomationAsync(string automationId, string revisionId, CancellationToken ct = default)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = GrantSelectColumns + " WHERE automation_id=$auto AND revision_id=$rev ORDER BY created_at_utc DESC";
        cmd.Parameters.AddWithValue("$auto", automationId);
        cmd.Parameters.AddWithValue("$rev", revisionId);
        var list = new List<PolicyGrant>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(ReadGrant(reader));
        return Result<IReadOnlyList<PolicyGrant>>.Ok(list);
    }

    public async Task<Result<IReadOnlyList<PolicyGrant>>> ListActiveGrantsAsync(
        string capabilityStableId, string sourceKind, string sourceId, string schemaSha256,
        DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = GrantSelectColumns + " WHERE capability_stable_id=$cap AND source_kind=$sk AND source_id=$sid AND schema_sha256=$schema AND revoked_at_utc IS NULL AND expires_at_utc > $now ORDER BY created_at_utc DESC";
        cmd.Parameters.AddWithValue("$cap", capabilityStableId);
        cmd.Parameters.AddWithValue("$sk", sourceKind);
        cmd.Parameters.AddWithValue("$sid", sourceId);
        cmd.Parameters.AddWithValue("$schema", schemaSha256);
        cmd.Parameters.AddWithValue("$now", nowUtc.ToString("O"));
        var list = new List<PolicyGrant>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(ReadGrant(reader));
        return Result<IReadOnlyList<PolicyGrant>>.Ok(list);
    }

    public async Task<Result<IReadOnlyList<PolicyGrant>>> ListActiveAsync(DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = GrantSelectColumns + " WHERE revoked_at_utc IS NULL AND expires_at_utc > $now ORDER BY created_at_utc DESC";
        cmd.Parameters.AddWithValue("$now", nowUtc.ToString("O"));
        var list = new List<PolicyGrant>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(ReadGrant(reader));
        return Result<IReadOnlyList<PolicyGrant>>.Ok(list);
    }

    public async Task<Result<PolicyGrant>> RevokeAsync(PolicyGrantId id, IClock clock, CancellationToken ct = default)
    {
        var existing = await ReadGrantByIdAsync(id, ct);
        if (existing is null)
            return Result<PolicyGrant>.Fail(PolicyErrors.GrantNotFoundError(id.Value));
        if (existing.RevokedAtUtc is not null)
            return Result<PolicyGrant>.Ok(existing); // idempotent: already revoked

        var now = clock.UtcNow.ToString("O");
        await using var tx = await _connection.BeginTransactionAsync(ct);
        try
        {
            var upd = _connection.CreateCommand();
            upd.Transaction = (SqliteTransaction)tx;
            upd.CommandText = "UPDATE policy_grants SET revoked_at_utc=$rev WHERE grant_id=$id";
            upd.Parameters.AddWithValue("$rev", now);
            upd.Parameters.AddWithValue("$id", id.Value);
            await upd.ExecuteNonQueryAsync(ct);
            await WriteGrantAuditAsync(tx, clock, PolicyAuditAction.GrantRevoked,
                $"{{\"grant_id\":\"{id.Value}\"}}", ct);
            await tx.CommitAsync(ct);
            return Result<PolicyGrant>.Ok(existing with { RevokedAtUtc = clock.UtcNow });
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // ---- internals ----

    private IReadOnlyList<PolicyStatement> RebindStatements(IReadOnlyList<PolicyStatement> templates, PolicyVersionId versionId)
    {
        var rebuilt = new List<PolicyStatement>(templates.Count);
        foreach (var s in templates)
        {
            rebuilt.Add(PolicyStatement.Create(
                _ids, versionId, s.Enabled, s.Effect, s.Subjects, s.SourceSelectorJson,
                s.CapabilitySelectorJson, s.RiskMin, s.RiskMax, s.Scope, s.Conditions, s.Priority));
        }

        return rebuilt;
    }

    private static (string Canonical, string Hash) Canonicalize(IReadOnlyList<PolicyStatement> statements)
        => (PolicyCanonicalizer.CanonicalizeStatements(statements), PolicyCanonicalizer.HashStatements(statements));

    private async Task<PolicyDocument?> ReadDocumentAsync(PolicyDocumentId id, CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id,layer,scope_id,current_version_id,created_at_utc,updated_at_utc FROM policy_documents WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id.Value);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadDocument(reader) : null;
    }

    private async Task<PolicyDocument?> ReadDocumentByLayerScopeAsync(PolicyLayer layer, string? scopeId, CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id,layer,scope_id,current_version_id,created_at_utc,updated_at_utc FROM policy_documents WHERE layer=$layer AND scope_id IS $scope";
        cmd.Parameters.AddWithValue("$layer", layer.ToString());
        cmd.Parameters.AddWithValue("$scope", (object?)scopeId ?? DBNull.Value);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadDocument(reader) : null;
    }

    private static PolicyDocument ReadDocument(DbDataReader reader)
        => new(
            PolicyDocumentId.Parse(reader.GetString(0)),
            Enum.Parse<PolicyLayer>(reader.GetString(1)),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : PolicyVersionId.Parse(reader.GetString(3)),
            DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
            DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal));

    private async Task<int> NextVersionNumberAsync(PolicyDocumentId docId, CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(version_number),0) FROM policy_versions WHERE document_id=$id";
        cmd.Parameters.AddWithValue("$id", docId.Value);
        var max = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        return max + 1;
    }

    private async Task<string?> ReadVersionHashAsync(PolicyVersionId versionId, CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT canonical_sha256 FROM policy_versions WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", versionId.Value);
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is DBNull or null ? null : (string)v;
    }

    private async Task<PolicyVersion?> ReadVersionAsync(PolicyVersionId versionId, CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id,document_id,version_number,canonical_sha256,document_json,is_default,created_at_utc FROM policy_versions WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", versionId.Value);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return new PolicyVersion(
            PolicyVersionId.Parse(reader.GetString(0)),
            PolicyDocumentId.Parse(reader.GetString(1)),
            reader.GetInt32(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt32(5) == 1,
            DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal));
    }

    private async Task InsertDocumentAsync(DbTransaction tx, string docId, PolicyLayer layer, string? scopeId, string now, CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText = "INSERT INTO policy_documents(id,layer,scope_id,current_version_id,created_at_utc,updated_at_utc,row_version) VALUES($id,$layer,$scope,NULL,$now,$now,1)";
        cmd.Parameters.AddWithValue("$id", docId);
        cmd.Parameters.AddWithValue("$layer", layer.ToString());
        cmd.Parameters.AddWithValue("$scope", (object?)scopeId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", now);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task InsertVersionAsync(DbTransaction tx, PolicyVersionId versionId, PolicyDocumentId docId, int number,
        string hash, string canonical, bool isDefault, string now, CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText = "INSERT INTO policy_versions(id,document_id,version_number,canonical_sha256,document_json,is_default,created_at_utc) VALUES($id,$doc,$num,$hash,$docJson,$def,$now)";
        cmd.Parameters.AddWithValue("$id", versionId.Value);
        cmd.Parameters.AddWithValue("$doc", docId.Value);
        cmd.Parameters.AddWithValue("$num", number);
        cmd.Parameters.AddWithValue("$hash", hash);
        cmd.Parameters.AddWithValue("$docJson", canonical);
        cmd.Parameters.AddWithValue("$def", isDefault ? 1 : 0);
        cmd.Parameters.AddWithValue("$now", now);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task InsertStatementsAsync(DbTransaction tx, PolicyVersionId versionId, IReadOnlyList<PolicyStatement> statements, string now, CancellationToken ct)
    {
        foreach (var s in statements)
        {
            var cmd = _connection.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = """
                INSERT INTO policy_statements(id,version_id,enabled,effect,subjects,source_selector_json,capability_selector_json,risk_min,risk_max,resource_scope_json,conditions_json,priority,created_at_utc)
                VALUES($id,$ver,$enabled,$effect,$subjects,$src,$cap,$rmin,$rmax,$scope,$conds,$prio,$now)
                """;
            cmd.Parameters.AddWithValue("$id", s.Id.Value);
            cmd.Parameters.AddWithValue("$ver", versionId.Value);
            cmd.Parameters.AddWithValue("$enabled", s.Enabled ? 1 : 0);
            cmd.Parameters.AddWithValue("$effect", s.Effect.ToString());
            cmd.Parameters.AddWithValue("$subjects", SerializeSubjects(s.Subjects));
            cmd.Parameters.AddWithValue("$src", s.SourceSelectorJson);
            cmd.Parameters.AddWithValue("$cap", s.CapabilitySelectorJson);
            cmd.Parameters.AddWithValue("$rmin", (int)s.RiskMin);
            cmd.Parameters.AddWithValue("$rmax", (int)s.RiskMax);
            cmd.Parameters.AddWithValue("$scope", s.Scope is null ? (object)DBNull.Value : s.Scope.ToStorageJson());
            cmd.Parameters.AddWithValue("$conds", SerializeConditions(s.Conditions));
            cmd.Parameters.AddWithValue("$prio", s.Priority);
            cmd.Parameters.AddWithValue("$now", now);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private async Task UpdateCurrentVersionAsync(DbTransaction tx, PolicyDocumentId docId, PolicyVersionId versionId, string now, CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText = "UPDATE policy_documents SET current_version_id=$ver, updated_at_utc=$now WHERE id=$id";
        cmd.Parameters.AddWithValue("$ver", versionId.Value);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.AddWithValue("$id", docId.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task WriteAuditAsync(DbTransaction tx, IClock clock, PolicyLayer? layer, PolicyAuditAction action,
        PolicyAuditSource source, PolicyDocumentId? docId, PolicyVersionId? versionId, string? reasonCode, string? actor,
        string? policyHash, string detailJson, CancellationToken ct)
    {
        var occurred = clock.UtcNow.ToString("O");
        var cmd = _connection.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText = """
            INSERT INTO policy_audit(id,occurred_at_utc,layer,action,document_id,version_id,reason_code,actor,source,detail_json,policy_hash,created_at_utc)
            VALUES($id,$occ,$layer,$action,$doc,$ver,$reason,$actor,$source,$detail,$hash,$created)
            """;
        cmd.Parameters.AddWithValue("$id", PolicyAuditId.Create(_ids).Value);
        cmd.Parameters.AddWithValue("$occ", occurred);
        cmd.Parameters.AddWithValue("$layer", layer is null ? (object)DBNull.Value : layer.Value.ToString());
        cmd.Parameters.AddWithValue("$action", AuditActionToString(action));
        cmd.Parameters.AddWithValue("$doc", docId is null ? (object)DBNull.Value : docId.Value.Value);
        cmd.Parameters.AddWithValue("$ver", versionId is null ? (object)DBNull.Value : versionId.Value.Value);
        cmd.Parameters.AddWithValue("$reason", (object?)reasonCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$actor", (object?)actor ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$source", AuditSourceToString(source));
        cmd.Parameters.AddWithValue("$detail", detailJson);
        cmd.Parameters.AddWithValue("$hash", (object?)policyHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", occurred);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task InvalidateReceiptsCoreAsync(DbTransaction tx, string oldHash, string now, CancellationToken ct)
    {
        var update = _connection.CreateCommand();
        update.Transaction = (SqliteTransaction)tx;
        update.CommandText = "UPDATE consent_receipts SET status='invalidated' WHERE policy_hash=$hash AND status='issued'";
        update.Parameters.AddWithValue("$hash", oldHash);
        await update.ExecuteNonQueryAsync(ct);

        var audit = _connection.CreateCommand();
        audit.Transaction = (SqliteTransaction)tx;
        audit.CommandText = """
            INSERT INTO policy_audit(id,occurred_at_utc,layer,action,document_id,version_id,reason_code,actor,source,detail_json,policy_hash,created_at_utc)
            VALUES($id,$occ,NULL,'receipt_invalidated',NULL,NULL,'policy_hash_superseded',NULL,'receipt',$detail,$hash,$occ)
            """;
        audit.Parameters.AddWithValue("$id", PolicyAuditId.Create(_ids).Value);
        audit.Parameters.AddWithValue("$occ", now);
        audit.Parameters.AddWithValue("$detail", $"{{\"policy_hash\":\"{oldHash}\"}}");
        audit.Parameters.AddWithValue("$hash", oldHash);
        await audit.ExecuteNonQueryAsync(ct);
    }

    private const string GrantSelectColumns =
        "SELECT grant_id,automation_id,revision_id,space_id,expert_revision_id,source_kind,source_id,capability_stable_id,schema_sha256,resource_scope_json,scope_sha256,risk_ceiling,not_before_utc,expires_at_utc,revocation_epoch_at_issue,created_at_utc,revoked_at_utc FROM policy_grants";

    private async Task InsertGrantAsync(DbTransaction tx, PolicyGrant g, string now, CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText = """
            INSERT INTO policy_grants(grant_id,automation_id,revision_id,space_id,expert_revision_id,source_kind,source_id,capability_stable_id,schema_sha256,resource_scope_json,scope_sha256,risk_ceiling,not_before_utc,expires_at_utc,revocation_epoch_at_issue,created_at_utc,revoked_at_utc)
            VALUES($id,$auto,$rev,$space,$exp,$sk,$sid,$cap,$schema,$scope,$scopeHash,$risk,$nb,$expAt,$epoch,$now,$revAt)
            """;
        cmd.Parameters.AddWithValue("$id", g.GrantId.Value);
        cmd.Parameters.AddWithValue("$auto", g.AutomationId);
        cmd.Parameters.AddWithValue("$rev", g.RevisionId);
        cmd.Parameters.AddWithValue("$space", (object?)g.SpaceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$exp", (object?)g.ExpertRevisionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sk", g.SourceKind);
        cmd.Parameters.AddWithValue("$sid", g.SourceId);
        cmd.Parameters.AddWithValue("$cap", g.CapabilityStableId);
        cmd.Parameters.AddWithValue("$schema", g.SchemaSha256);
        cmd.Parameters.AddWithValue("$scope", g.ResourceScope.ToStorageJson());
        cmd.Parameters.AddWithValue("$scopeHash", g.ScopeSha256);
        cmd.Parameters.AddWithValue("$risk", (int)g.RiskCeiling);
        cmd.Parameters.AddWithValue("$nb", g.NotBeforeUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$expAt", g.ExpiresAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$epoch", g.RevocationEpochAtIssue);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.AddWithValue("$revAt", (object?)g.RevokedAtUtc?.ToString("O") ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<PolicyGrant?> ReadGrantByIdAsync(PolicyGrantId id, CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = GrantSelectColumns + " WHERE grant_id=$id";
        cmd.Parameters.AddWithValue("$id", id.Value);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadGrant(reader) : null;
    }

    private static PolicyGrant ReadGrant(DbDataReader reader)
        => new(
            PolicyGrantId.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            ResourceScope.FromStorageJson(reader.GetString(9)),
            reader.GetString(10),
            (RiskLevel)reader.GetInt32(11),
            DateTimeOffset.Parse(reader.GetString(12), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
            DateTimeOffset.Parse(reader.GetString(13), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
            reader.GetInt64(14),
            DateTimeOffset.Parse(reader.GetString(15), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
            reader.IsDBNull(16) ? (DateTimeOffset?)null : DateTimeOffset.Parse(reader.GetString(16), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal));

    private async Task WriteGrantAuditAsync(DbTransaction tx, IClock clock, PolicyAuditAction action, string detailJson, CancellationToken ct)
        => await WriteAuditAsync(tx, clock, null, action, PolicyAuditSource.Grant, null, null, action.ToString().ToLowerInvariant(), "system", null, detailJson, ct);

    private async Task<IReadOnlyList<PolicyStatement>> ReadStatementsAsync(PolicyVersionId versionId, CancellationToken ct)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id,enabled,effect,subjects,source_selector_json,capability_selector_json,risk_min,risk_max,resource_scope_json,conditions_json,priority FROM policy_statements WHERE version_id=$ver ORDER BY id";
        cmd.Parameters.AddWithValue("$ver", versionId.Value);
        var list = new List<PolicyStatement>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(ReadStatement(reader, versionId));
        return list;
    }

    private static PolicyStatement ReadStatement(DbDataReader reader, PolicyVersionId versionId)
        => new(
            PolicyStatementId.Parse(reader.GetString(0)),
            versionId,
            reader.GetInt32(1) == 1,
            Enum.Parse<PolicyEffect>(reader.GetString(2), ignoreCase: true),
            ParseSubjects(reader.GetString(3)),
            reader.GetString(4),
            reader.GetString(5),
            (RiskLevel)reader.GetInt32(6),
            (RiskLevel)reader.GetInt32(7),
            reader.IsDBNull(8) ? null : ResourceScope.FromStorageJson(reader.GetString(8)),
            ParseConditions(reader.GetString(9)),
            reader.GetInt32(10));

    private PolicyAuditRecord ReadAudit(DbDataReader reader)
        => PolicyAuditRecord.Create(
            _ids,
            DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
            reader.IsDBNull(2) ? null : Enum.Parse<PolicyLayer>(reader.GetString(2)),
            AuditActionFromString(reader.GetString(3)),
            reader.IsDBNull(4) ? null : PolicyDocumentId.Parse(reader.GetString(4)),
            reader.IsDBNull(5) ? null : PolicyVersionId.Parse(reader.GetString(5)),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            AuditSourceFromString(reader.GetString(8)),
            reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10));

    private static string SerializeSubjects(IReadOnlyList<PolicySubject> subjects)
    {
        var arr = new JsonArray();
        foreach (var s in subjects)
            arr.Add(s.ToString().ToLowerInvariant());
        return arr.ToJsonString();
    }

    private static IReadOnlyList<PolicySubject> ParseSubjects(string json)
    {
        var node = JsonNode.Parse(json)!;
        var result = new List<PolicySubject>();
        if (node is JsonArray array)
            foreach (var item in array)
                if (item is not null && item.GetValueKind() == JsonValueKind.String)
                    result.Add(Enum.Parse<PolicySubject>((string)item!, ignoreCase: true));
        return result;
    }

    private static string SerializeConditions(IReadOnlyList<PolicyCondition> conditions)
    {
        var arr = new JsonArray();
        foreach (var c in conditions)
        {
            var obj = new JsonObject
            {
                ["kind"] = c.Kind.ToString().ToLowerInvariant() switch
                {
                    "timewindow" => "time_window",
                    "daysofweek" => "days_of_week",
                    "runmode" => "run_mode",
                    "triggertype" => "trigger_type",
                    "targetcountmax" => "target_count_max",
                    "resultsizemax" => "result_size_max",
                    "sourcehealthin" => "source_health_in",
                    _ => "unknown"
                }
            };
            obj["detail"] = JsonNode.Parse(c.DetailJson);
            arr.Add(obj);
        }

        return arr.ToJsonString();
    }

    private static IReadOnlyList<PolicyCondition> ParseConditions(string json)
    {
        var node = JsonNode.Parse(json)!;
        var result = new List<PolicyCondition>();
        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not JsonObject obj)
                    continue;
                var kindStr = obj["kind"]?.GetValueKind() == JsonValueKind.String ? (string)obj["kind"]! : "unknown";
                var detailNode = obj["detail"];
                var detailJson = detailNode is null ? "{}" : detailNode.ToJsonString();
                result.Add(new PolicyCondition(MapConditionKind(kindStr), detailJson));
            }
        }

        return result;
    }

    private static PolicyConditionKind MapConditionKind(string kind) => kind switch
    {
        "time_window" => PolicyConditionKind.TimeWindow,
        "days_of_week" => PolicyConditionKind.DaysOfWeek,
        "run_mode" => PolicyConditionKind.RunMode,
        "trigger_type" => PolicyConditionKind.TriggerType,
        "target_count_max" => PolicyConditionKind.TargetCountMax,
        "result_size_max" => PolicyConditionKind.ResultSizeMax,
        "source_health_in" => PolicyConditionKind.SourceHealthIn,
        _ => PolicyConditionKind.Unknown
    };

    // Stored as lowercase to satisfy the policy_audit CHECK constraints (doc 07 / migration 019).
    private static string AuditActionToString(PolicyAuditAction action) => action switch
    {
        PolicyAuditAction.Bootstrap => "bootstrap",
        PolicyAuditAction.Recovery => "recovery",
        PolicyAuditAction.UserSave => "user_save",
        PolicyAuditAction.GrantIssued => "grant_issued",
        PolicyAuditAction.GrantRevoked => "grant_revoked",
        PolicyAuditAction.ReceiptConsumed => "receipt_consumed",
        PolicyAuditAction.ReceiptInvalidated => "receipt_invalidated",
        PolicyAuditAction.LegacyV14 => "legacy_v14",
        PolicyAuditAction.IntegrityCheck => "integrity_check",
        _ => "user_save"
    };

    private static string AuditSourceToString(PolicyAuditSource source) => source switch
    {
        PolicyAuditSource.Bootstrap => "bootstrap",
        PolicyAuditSource.Recovery => "recovery",
        PolicyAuditSource.UserSave => "user_save",
        PolicyAuditSource.Grant => "grant",
        PolicyAuditSource.Receipt => "receipt",
        PolicyAuditSource.LegacyV14 => "legacy_v14",
        _ => "user_save"
    };

    private static PolicyAuditAction AuditActionFromString(string s) => s switch
    {
        "bootstrap" => PolicyAuditAction.Bootstrap,
        "recovery" => PolicyAuditAction.Recovery,
        "user_save" => PolicyAuditAction.UserSave,
        "grant_issued" => PolicyAuditAction.GrantIssued,
        "grant_revoked" => PolicyAuditAction.GrantRevoked,
        "receipt_consumed" => PolicyAuditAction.ReceiptConsumed,
        "receipt_invalidated" => PolicyAuditAction.ReceiptInvalidated,
        "legacy_v14" => PolicyAuditAction.LegacyV14,
        "integrity_check" => PolicyAuditAction.IntegrityCheck,
        _ => PolicyAuditAction.UserSave
    };

    private static PolicyAuditSource AuditSourceFromString(string s) => s switch
    {
        "bootstrap" => PolicyAuditSource.Bootstrap,
        "recovery" => PolicyAuditSource.Recovery,
        "user_save" => PolicyAuditSource.UserSave,
        "grant" => PolicyAuditSource.Grant,
        "receipt" => PolicyAuditSource.Receipt,
        "legacy_v14" => PolicyAuditSource.LegacyV14,
        _ => PolicyAuditSource.UserSave
    };
}
