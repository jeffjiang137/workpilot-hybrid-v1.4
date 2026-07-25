using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WorkPilot.Models;

namespace WorkPilot.Services;

public sealed record CompiledAgentContext(
    AgentRunSnapshot Snapshot, Expert Expert, ExpertRevision Revision,
    string SystemInstruction, IReadOnlyList<SkillActivationEvidence> SkillEvidence);

public sealed class AgentContextService(
    DatabaseService database, ExpertService experts, SkillService skills)
{
    public async Task<CompiledAgentContext> CompileAsync(string? requestedExpertId, string conversationId,
        string spaceId, string? projectId, string modelId, string userMessage,
        IReadOnlySet<string> availableCapabilities, CancellationToken cancellationToken)
    {
        var expert = requestedExpertId is null ? null : await experts.GetAsync(requestedExpertId, cancellationToken);
        expert ??= (await experts.ListAsync(false, cancellationToken)).First();
        var revision = await experts.GetCurrentRevisionAsync(expert.Id, cancellationToken);
        var candidates = await LoadCandidatesAsync(expert.Id, cancellationToken);
        var evidence = SkillSelector.Select(userMessage, candidates, availableCapabilities);
        var selected = evidence.Where(x => x.ExclusionReason is null).ToList();
        var selectedVersionIds = selected.Select(x => candidates.First(c => c.SkillId == x.SkillId).VersionId).ToList();
        var instruction = await CompileInstructionAsync(revision, selectedVersionIds, cancellationToken);
        var now = DateTimeOffset.UtcNow; var snapshotId = Guid.NewGuid().ToString("N");
        var catalog = availableCapabilities.OrderBy(x => x, StringComparer.Ordinal).Take(64).ToList();
        var payload = JsonSerializer.Serialize(new
        {
            conversationId, revision.Id, spaceId, projectId, modelId,
            skills = selectedVersionIds, capabilities = catalog
        });
        var hash = Sha256(payload);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO agent_run_snapshots(id,conversation_id,expert_revision_id,space_id,project_id,
              task_id,model_id,selected_skills_json,capability_catalog_json,snapshot_sha256,created_at_utc)
            VALUES($id,$conversation,$revision,$space,$project,NULL,$model,$skills,$catalog,$hash,$now)
            """;
        command.Parameters.AddWithValue("$id", snapshotId); command.Parameters.AddWithValue("$conversation", conversationId);
        command.Parameters.AddWithValue("$revision", revision.Id); command.Parameters.AddWithValue("$space", spaceId);
        command.Parameters.AddWithValue("$project", (object?)projectId ?? DBNull.Value); command.Parameters.AddWithValue("$model", modelId);
        command.Parameters.AddWithValue("$skills", JsonSerializer.Serialize(selectedVersionIds));
        command.Parameters.AddWithValue("$catalog", JsonSerializer.Serialize(catalog)); command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$now", now.ToString("O")); await command.ExecuteNonQueryAsync(cancellationToken);
        var snapshot = new AgentRunSnapshot(snapshotId, conversationId, revision.Id, spaceId, projectId,
            modelId, selectedVersionIds, catalog, hash, now);
        return new(snapshot, expert, revision, instruction, evidence);
    }

    private async Task<List<SkillCandidate>> LoadCandidatesAsync(string expertId, CancellationToken cancellationToken)
    {
        var result = new List<SkillCandidate>();
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.id,v.id,v.semantic_version,s.display_name,v.description,v.manifest_json,
                   es.sort_order,es.activation_mode FROM expert_skills es
            JOIN skill_versions v ON v.id=es.skill_version_id JOIN skills s ON s.id=v.skill_id
            WHERE es.expert_id=$expert AND es.enabled=1 AND s.status='enabled' AND v.validation_status='valid'
            ORDER BY es.sort_order LIMIT 20
            """;
        command.Parameters.AddWithValue("$expert", expertId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var manifest = JsonSerializer.Deserialize<SkillManifest>(reader.GetString(5),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException("技能 manifest 无效");
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), manifest.Activation?.Aliases ?? [], manifest.Activation?.Tags ?? [],
                reader.GetInt32(6), reader.GetString(7) == "pinned", manifest.RequiredCapabilities ?? []));
        }
        return result;
    }

    private async Task<string> CompileInstructionAsync(ExpertRevision revision,
        IReadOnlyList<string> versionIds, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(); builder.AppendLine(revision.SystemInstruction);
        var total = 0;
        foreach (var versionId in versionIds)
        {
            var content = await skills.ReadInstructionAsync(versionId, cancellationToken);
            if (content.Length > 8_000) content = content[..8_000];
            if (total + content.Length > 24_000) throw new InvalidOperationException("固定技能指令超过 24,000 字符预算，请减少专家绑定技能");
            total += content.Length;
            builder.AppendLine().AppendLine("<SKILL_INSTRUCTION>").AppendLine(content).AppendLine("</SKILL_INSTRUCTION>");
        }
        return builder.ToString();
    }

    private static string Sha256(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
