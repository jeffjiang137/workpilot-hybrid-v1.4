using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WorkPilot.Models;

namespace WorkPilot.Services;

public sealed class CapabilityRuntimeService(
    DatabaseService database, ConnectorService connectors, McpService mcp)
{
    public async Task<IReadOnlyList<RuntimeCapability>> GetCatalogAsync(string spaceId, string expertId,
        CancellationToken cancellationToken = default)
    {
        var result = new List<RuntimeCapability>();
        foreach (var (account, capability) in await connectors.GetAvailableCapabilitiesAsync(spaceId, expertId, cancellationToken))
        {
            var schema = capability.InputSchema.GetRawText();
            result.Add(new(ToModelName(capability.StableId), capability.StableId, SourceKind.Connector,
                account.Id, account.DisplayName, capability.Title, capability.Description, capability.Risk,
                capability.Mutating, schema, Sha256(schema), capability.StableId));
        }
        foreach (var (server, capability) in await mcp.GetAvailableCapabilitiesAsync(spaceId, expertId, cancellationToken))
            result.Add(new(ToModelName(capability.StableName), capability.StableName, SourceKind.Mcp,
                server.Id, server.DisplayName, capability.Title, capability.Description, capability.LocalRisk,
                capability.LocalRisk >= RiskLevel.High, capability.InputSchemaJson, capability.SchemaSha256, capability.Id));
        return result.OrderBy(x => x.SourceKind).ThenBy(x => x.Title).Take(64).ToList();
    }

    public async Task<CapabilityResult> InvokeAsync(AgentRunSnapshot snapshot, RuntimeCapability capability,
        string argumentsJson, bool confirmed, CancellationToken cancellationToken)
    {
        if (capability.Risk == RiskLevel.Critical) throw new InvalidOperationException("此能力被 V1.4 安全策略阻止");
        if (capability.Risk >= RiskLevel.High && !confirmed) throw new UnauthorizedAccessException("高风险能力需要本次确认");
        var expertId = await ResolveExpertIdAsync(snapshot.ExpertRevisionId, cancellationToken);
        var current = (await GetCatalogAsync(snapshot.SpaceId, expertId, cancellationToken)).SingleOrDefault(x =>
            x.SourceKind == capability.SourceKind && x.SourceId == capability.SourceId &&
            x.StableId == capability.StableId);
        if (current is null || current.SchemaSha256 != capability.SchemaSha256)
            throw new UnauthorizedAccessException("能力授权已撤销或 Schema 已变化，请刷新后重新审查");
        var arguments = JsonSchemaGuard.ValidateObject(capability.SchemaJson, argumentsJson);
        if (confirmed) await RecordConsentAsync(snapshot, capability, cancellationToken);
        var stopwatch = Stopwatch.StartNew(); string outcome = "failed"; string? errorCategory = null;
        try
        {
            var result = capability.SourceKind == SourceKind.Connector
                ? await connectors.InvokeAsync(capability.SourceId, capability.StableId, arguments, cancellationToken)
                : await mcp.InvokeAsync(capability.SourceId, capability.InternalCapabilityId,
                    capability.SchemaSha256, arguments, cancellationToken);
            outcome = result.Success ? "success" : "failed"; errorCategory = result.ErrorCategory;
            return result;
        }
        catch (OperationCanceledException) { outcome = "cancelled"; errorCategory = "UserCancelled"; throw; }
        catch (UnauthorizedAccessException) { errorCategory = "PolicyDenied"; throw; }
        catch (HttpRequestException) { errorCategory = "Network"; throw; }
        catch (Exception) { errorCategory = "Internal"; throw; }
        finally
        {
            stopwatch.Stop(); await AuditAsync(snapshot, capability, confirmed ? "confirmed" : "policy",
                outcome, errorCategory, stopwatch.ElapsedMilliseconds, CancellationToken.None);
        }
    }

    private async Task<string> ResolveExpertIdAsync(string revisionId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT expert_id FROM expert_revisions WHERE id=$id";
        command.Parameters.AddWithValue("$id", revisionId);
        return await command.ExecuteScalarAsync(cancellationToken) as string
            ?? throw new UnauthorizedAccessException("运行快照的专家修订已失效");
    }

    public async Task<IReadOnlyList<CapabilityAudit>> GetAuditAsync(int limit = 500,
        CancellationToken cancellationToken = default)
    {
        var result = new List<CapabilityAudit>(); await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand(); command.CommandText = """
            SELECT id,run_snapshot_id,expert_id,space_id,source_kind,source_id,capability_stable_id,
                   risk_level,decision,outcome,error_category,duration_ms,result_size,created_at_utc
            FROM capability_audit ORDER BY created_at_utc DESC LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(new(reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.GetString(5),
            reader.GetString(6), (RiskLevel)reader.GetInt32(7), reader.GetString(8), reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10), reader.GetInt64(11), reader.GetInt64(12),
            DateTimeOffset.Parse(reader.GetString(13))));
        return result;
    }

    private async Task AuditAsync(AgentRunSnapshot snapshot, RuntimeCapability capability,
        string decision, string outcome, string? error, long duration, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await database.OpenConnectionAsync(cancellationToken); var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO capability_audit(run_snapshot_id,expert_id,space_id,source_kind,source_id,
                  capability_stable_id,risk_level,decision,outcome,error_category,duration_ms,result_size,created_at_utc)
                SELECT $run,r.expert_id,$space,$kind,$source,$capability,$risk,$decision,$outcome,$error,$duration,0,$now
                FROM expert_revisions r WHERE r.id=$revision
                """;
            command.Parameters.AddWithValue("$run", snapshot.Id); command.Parameters.AddWithValue("$space", snapshot.SpaceId);
            command.Parameters.AddWithValue("$kind", capability.SourceKind.ToString().ToLowerInvariant()); command.Parameters.AddWithValue("$source", capability.SourceId);
            command.Parameters.AddWithValue("$capability", capability.StableId); command.Parameters.AddWithValue("$risk", (int)capability.Risk);
            command.Parameters.AddWithValue("$decision", decision); command.Parameters.AddWithValue("$outcome", outcome);
            command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value); command.Parameters.AddWithValue("$duration", duration);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); command.Parameters.AddWithValue("$revision", snapshot.ExpertRevisionId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception auditError) { AppLogger.Error("Capability audit failed", auditError); }
    }

    private async Task RecordConsentAsync(AgentRunSnapshot snapshot, RuntimeCapability capability,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken); var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO consent_receipts(id,run_snapshot_id,source_kind,source_id,capability_stable_id,
              schema_sha256,risk_level,scope,expires_at_utc,decision,created_at_utc)
            VALUES($id,$run,$kind,$source,$capability,$schema,$risk,'once',$expires,'allow',$now)
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N")); command.Parameters.AddWithValue("$run", snapshot.Id);
        command.Parameters.AddWithValue("$kind", capability.SourceKind.ToString().ToLowerInvariant()); command.Parameters.AddWithValue("$source", capability.SourceId);
        command.Parameters.AddWithValue("$capability", capability.StableId); command.Parameters.AddWithValue("$schema", capability.SchemaSha256);
        command.Parameters.AddWithValue("$risk", (int)capability.Risk); command.Parameters.AddWithValue("$expires", DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string ToModelName(string stable)
    {
        var safe = new string(stable.Select(x => char.IsAsciiLetterOrDigit(x) || x is '_' or '-' ? x : '_').ToArray());
        if (safe.Length <= 64) return safe;
        return safe[..57] + "_" + Sha256(stable)[..6];
    }

    private static string Sha256(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
