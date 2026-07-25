using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation;

namespace WorkPilot.Application.Automation.Definition;

/// <summary>Port: export a single automation definition + its current revision as a portable JSON.</summary>
public interface IDefinitionExporter
{
    Task<Result<DefinitionExport>> ExportAsync(AutomationId id, CancellationToken ct = default);
}

/// <summary>
/// Builds a non-secret, machine-readable <see cref="DefinitionExport"/> (AUT-006). The envelope never
/// contains a credential, grant or run id; capability permissions are derived from the workflow's
/// <c>capability_call</c> nodes. <see cref="DefinitionExport.CanonicalHash"/> is a SHA-256 over the
/// exact emitted JSON so a receiver can verify integrity.
/// </summary>
public sealed class DefinitionExporter : IDefinitionExporter
{
    private readonly IAutomationRepository _repo;
    private readonly IClock _clock;

    public DefinitionExporter(IAutomationRepository repo, IClock clock)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<Result<DefinitionExport>> ExportAsync(AutomationId id, CancellationToken ct = default)
    {
        var defResult = await _repo.GetAsync(id, ct).ConfigureAwait(false);
        if (!defResult.IsSuccess) return Result<DefinitionExport>.Fail(defResult.Error!);
        var def = defResult.Value!;

        var revResult = await _repo.GetRevisionAsync(def.CurrentRevisionId, ct).ConfigureAwait(false);
        if (!revResult.IsSuccess) return Result<DefinitionExport>.Fail(revResult.Error!);
        var rev = revResult.Value!;

        var envelope = BuildEnvelope(def, rev);
        var json = envelope.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var hash = Sha256Hex(json);
        var fileName = $"workpilot-definition-{Sanitize(def.Name)}-{def.RevisionNumber}.json";
        return Result<DefinitionExport>.Ok(new DefinitionExport(json, fileName, hash, def.RevisionNumber, _clock.UtcNow));
    }

    private static JsonObject BuildEnvelope(AutomationDefinition def, AutomationRevision rev)
    {
        return new JsonObject
        {
            ["schema_version"] = 1,
            ["name"] = def.Name,
            ["description"] = def.Description,
            ["binding"] = new JsonObject
            {
                ["space_id"] = def.SpaceId.Value,
                ["project_id"] = (JsonNode?)rev.Binding.ProjectId,
                ["expert_id"] = (JsonNode?)rev.Binding.ExpertId,
                ["model_policy"] = "expert_revision"
            },
            ["trigger"] = BuildTrigger(rev.Trigger),
            ["workflow"] = BuildWorkflow(rev.Workflow),
            ["budget"] = BuildBudget(rev.Budget),
            ["overlap_policy"] = rev.OverlapPolicy.ToStorage(),
            ["missed_run_policy"] = rev.MissedRunPolicy.ToStorage(),
            ["permission_request"] = BuildPermissionRequest(rev.Workflow)
        };
    }

    private static JsonObject BuildTrigger(TriggerDefinition t)
    {
        var obj = new JsonObject
        {
            ["trigger_id"] = t.TriggerId,
            ["type"] = t.Type.ToStorage(),
            ["enabled"] = t.Enabled
        };
        if (t.TimezoneId is { } tz) obj["timezone_id"] = tz;
        switch (t.Type)
        {
            case TriggerType.Interval:
                if (t.IntervalSeconds is { } i) obj["interval_seconds"] = i;
                if (t.AnchorAtUtc is { } aInterval) obj["anchor_at_utc"] = aInterval.ToString("O", CultureInfo.InvariantCulture);
                break;
            case TriggerType.Once:
                if (t.AnchorAtUtc is { } aOnce)
                {
                    obj["local_date"] = aOnce.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    obj["local_time"] = aOnce.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture);
                }
                break;
            case TriggerType.CalendarDaily:
            case TriggerType.CalendarWeekly:
                if (t.LocalTime is { } ltDw) obj["local_time"] = ltDw;
                if (t.DaysOfWeek is { } dw) obj["days_of_week"] = new JsonArray(dw.Select(x => (JsonNode)(long)x).ToArray());
                break;
            case TriggerType.CalendarMonthly:
                if (t.LocalTime is { } ltMo) obj["local_time"] = ltMo;
                if (t.DayOfMonth is { } d) obj["day_of_month"] = d;
                if (t.MissingDay is { } m) obj["missing_day"] = m;
                break;
            case TriggerType.DomainEvent:
                if (t.EventType is { } ev) obj["event_type"] = ev;
                if (t.Filters is { } f) obj["filters"] = f.DeepClone();
                break;
        }
        return obj;
    }

    private static JsonObject BuildWorkflow(WorkflowDefinition w)
    {
        var nodes = new JsonArray();
        foreach (var n in w.Nodes)
        {
            var no = new JsonObject
            {
                ["node_id"] = n.NodeId,
                ["kind"] = n.Kind,
                ["display_name"] = n.DisplayName,
                ["timeout_seconds"] = n.TimeoutSeconds,
                ["disabled"] = n.Disabled
            };
            if (n.Payload is { } p) no["payload"] = p.DeepClone();
            nodes.Add(no);
        }
        var edges = new JsonArray();
        foreach (var e in w.Edges)
            edges.Add(new JsonObject
            {
                ["from_node_id"] = e.FromNodeId,
                ["to_node_id"] = e.ToNodeId,
                ["branch"] = e.Branch
            });
        return new JsonObject
        {
            ["schema_version"] = w.SchemaVersion,
            ["entry_node_id"] = w.EntryNodeId,
            ["nodes"] = nodes,
            ["edges"] = edges
        };
    }

    private static JsonObject BuildBudget(RunBudget b)
        => new()
        {
            ["wall_clock_seconds"] = b.MaxWallClockSeconds,
            ["model_turns"] = b.MaxModelTurns,
            ["capability_calls"] = b.MaxCapabilityCalls,
            ["result_bytes"] = b.MaxResultBytes
        };

    private static JsonObject BuildPermissionRequest(WorkflowDefinition w)
    {
        var caps = new JsonArray();
        foreach (var node in w.Nodes)
        {
            if (node.Kind != "capability_call" || node.Payload?["capability"] is not JsonObject cap) continue;
            string? Source(string k) => cap.TryGetPropertyValue(k, out var v) && v is JsonValue jv ? jv.GetValue<string>() : null;
            caps.Add(new JsonObject
            {
                ["source_kind"] = (JsonNode?)Source("source_kind"),
                ["source_id"] = (JsonNode?)Source("source_id"),
                ["capability_stable_id"] = (JsonNode?)Source("stable_id"),
                ["schema_sha256"] = (JsonNode?)Source("schema_sha256"),
                ["resource_scope"] = (JsonNode?)(cap["resource_scope"] as JsonObject ?? new JsonObject())
            });
        }
        return new JsonObject { ["capabilities"] = caps };
    }

    private static string Sha256Hex(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string Sanitize(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name)
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        return sb.Length == 0 ? "definition" : sb.ToString();
    }
}
