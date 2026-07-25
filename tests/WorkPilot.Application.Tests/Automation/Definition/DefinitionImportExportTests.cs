using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation;
using WorkPilot.Application.Automation;
using WorkPilot.Application.Automation.Definition;
using Xunit;

namespace WorkPilot.Application.Tests.Automation.Definition;

public class DefinitionImportExportTests
{
    private const string SpaceIdHex = "11111111111111111111111111111111";
    private const string ExpertIdHex = "22222222222222222222222222222222";
    private const string ProjectIdHex = "33333333333333333333333333333333";
    private const string SchemaSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static readonly IIdGenerator Ids = new SequentialIdGenerator();
    private static readonly IClock Clock = new FixedClock();

    // ---- positive: export --------------------------------------------------

    [Fact]
    public async Task Export_produces_valid_envelope_with_no_secret_or_run_id()
    {
        var repo = new RecordingAutomationRepository();
        var (autoId, _) = SeedSample(repo);
        var exporter = new DefinitionExporter(repo, Clock);

        var result = await exporter.ExportAsync(autoId, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Code);
        var json = result.Value!.Json;
        Assert.Equal(64, result.Value.CanonicalHash.Length);
        Assert.Contains("Sample", result.Value.FileName);

        var env = JsonNode.Parse(json)!.AsObject();
        Assert.Equal(1, (int)env["schema_version"]!);
        Assert.Equal("Sample", (string?)env["name"]);
        Assert.Equal(SpaceIdHex, (string?)env["binding"]!["space_id"]);
        var nodeCount = env["workflow"]!["nodes"]!.AsArray().Count;
        var capCount = env["permission_request"]!["capabilities"]!.AsArray().Count;
        Assert.Equal(2, nodeCount);
        Assert.Equal(1, capCount);

        // AUT-A07: no credential / grant / run id ever leaves the export.
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("grant", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("run_id", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(autoId.Value, json);
    }

    // ---- positive: import ID rebuild + disabled ---------------------------

    [Fact]
    public async Task Import_rebuilds_ids_and_creates_disabled_draft()
    {
        var repo = new RecordingAutomationRepository();
        var (autoId, revId) = SeedSample(repo);
        var exporter = new DefinitionExporter(repo, Clock);
        var export = (await exporter.ExportAsync(autoId, CancellationToken.None)).Value!;
        var importer = new DefinitionImporter(repo, Ids, Clock);

        var imported = await importer.ImportAsync(export.Json, CancellationToken.None);

        Assert.True(imported.IsSuccess, imported.Error?.Code);
        Assert.NotEqual(autoId, imported.Value!.NewAutomationId);
        Assert.False(imported.Value.NeedsReview);
        var defCount = repo.Defs.Count;
        Assert.Equal(2, defCount);

        var importedDef = repo.Defs.Single(d => d.Id == imported.Value.NewAutomationId);
        Assert.Equal(AutomationLifecycle.Draft, importedDef.Lifecycle); // disabled on import

        // IDs genuinely rebuilt: the imported revision's trigger/node ids differ from the source.
        var importedRev = repo.Revisions[imported.Value.NewRevisionId];
        var sourceRev = repo.Revisions[revId];
        Assert.NotEqual(sourceRev.Trigger.TriggerId, importedRev.Trigger.TriggerId);
        Assert.NotEqual(sourceRev.Workflow.Nodes[0].NodeId, importedRev.Workflow.Nodes[0].NodeId);
    }

    [Fact]
    public async Task Import_then_export_round_trips_structure()
    {
        var repo = new RecordingAutomationRepository();
        var (autoId, _) = SeedSample(repo);
        var exporter = new DefinitionExporter(repo, Clock);
        var original = (await exporter.ExportAsync(autoId, CancellationToken.None)).Value!;
        var importer = new DefinitionImporter(repo, Ids, Clock);
        var imported = (await importer.ImportAsync(original.Json, CancellationToken.None)).Value!;
        var reExport = (await exporter.ExportAsync(imported.NewAutomationId, CancellationToken.None)).Value!;

        var a = JsonNode.Parse(original.Json)!.AsObject();
        var b = JsonNode.Parse(reExport.Json)!.AsObject();
        Assert.Equal((string?)a["name"], (string?)b["name"]);
        var ac = a["workflow"]!["nodes"]!.AsArray().Count;
        var bc = b["workflow"]!["nodes"]!.AsArray().Count;
        Assert.Equal(ac, bc);
        Assert.Equal((string?)a["trigger"]!["type"], (string?)b["trigger"]!["type"]);
        var aMt = (int?)a["budget"]!["model_turns"];
        var bMt = (int?)b["budget"]!["model_turns"];
        Assert.Equal(aMt, bMt);
        var aCap = a["permission_request"]!["capabilities"]!.AsArray().Count;
        var bCap = b["permission_request"]!["capabilities"]!.AsArray().Count;
        Assert.Equal(aCap, bCap);
    }

    // ---- negative: schema validation -------------------------------------

    [Fact]
    public void Validate_rejects_malformed_json()
    {
        var r = new DefinitionSchemaValidator().Validate("{ not json");
        Assert.False(r.IsSuccess);
        Assert.Equal("AUT_DEF_MALFORMED", r.Error!.Code);
    }

    [Fact]
    public void Validate_rejects_missing_required_binding()
    {
        var env = BuildEnvelope(triggerType: "manual");
        env.Remove("binding");
        var r = new DefinitionSchemaValidator().Validate(env.ToJsonString());
        Assert.False(r.IsSuccess);
        Assert.Equal("AUT_DEF_BINDING", r.Error!.Code);
    }

    [Fact]
    public void Validate_rejects_wrong_schema_version()
    {
        var env = BuildEnvelope(triggerType: "manual");
        env["schema_version"] = 2;
        var r = new DefinitionSchemaValidator().Validate(env.ToJsonString());
        Assert.False(r.IsSuccess);
        Assert.Equal("AUT_DEF_SCHEMA_VER", r.Error!.Code);
    }

    [Fact]
    public void Validate_rejects_secret_key_anywhere()
    {
        var env = BuildEnvelope(triggerType: "manual");
        env["binding"]!["api_key"] = "abc";
        var r = new DefinitionSchemaValidator().Validate(env.ToJsonString());
        Assert.False(r.IsSuccess);
        Assert.Equal("AUT_DEF_SECRET", r.Error!.Code);
    }

    [Fact]
    public void Validate_rejects_out_of_range_budget()
    {
        var env = BuildEnvelope(triggerType: "manual");
        env["budget"]!["wall_clock_seconds"] = 10; // below 60
        var r = new DefinitionSchemaValidator().Validate(env.ToJsonString());
        Assert.False(r.IsSuccess);
        Assert.Equal("AUT_DEF_BUDGET", r.Error!.Code);
    }

    // ---- unresolved warnings (AUT-A08) ------------------------------------

    [Fact]
    public async Task Import_missing_expert_id_is_unresolved_and_needs_review()
    {
        var repo = new RecordingAutomationRepository();
        var env = BuildEnvelope(triggerType: "manual", expertId: "");
        var parsed = new DefinitionSchemaValidator().Validate(env.ToJsonString());
        Assert.True(parsed.IsSuccess);
        Assert.True(parsed.Value!.NeedsReview);
        Assert.Contains(parsed.Value.Warnings, w => w.Kind == ImportWarningKind.UnresolvedSource);

        var importer = new DefinitionImporter(repo, Ids, Clock);
        var imported = await importer.ImportAsync(env.ToJsonString(), CancellationToken.None);
        Assert.True(imported.IsSuccess, imported.Error?.Code);
        Assert.True(imported.Value!.NeedsReview);
    }

    [Fact]
    public void Validate_missing_timezone_for_calendar_trigger_warns_unresolved()
    {
        var env = BuildEnvelope(triggerType: "calendar_daily", timezone: null);
        var parsed = new DefinitionSchemaValidator().Validate(env.ToJsonString());
        Assert.True(parsed.IsSuccess);
        Assert.True(parsed.Value!.NeedsReview);
        Assert.Contains(parsed.Value.Warnings, w => w.Kind == ImportWarningKind.UnresolvedTimezone);
    }

    // ---- helpers -----------------------------------------------------------

    private static (AutomationId, AutomationRevisionId) SeedSample(RecordingAutomationRepository repo)
    {
        var autoId = AutomationId.Create(Ids);
        var revId = AutomationRevisionId.Create(Ids);
        var trigger = new TriggerDefinition("trig_src", TriggerType.Manual, true, null, null, null, null, null, null, null, null, null, null, null);
        var agent = new WorkflowNode("n1", "Agent", "agent_prompt", 60, false, new JsonObject
        {
            ["instruction_template"] = "do it",
            ["output_key"] = "result",
            ["input_bindings"] = new JsonArray()
        });
        var cap = new WorkflowNode("n2", "Send", "capability_call", 60, false, new JsonObject
        {
            ["capability"] = new JsonObject
            {
                ["source_kind"] = "connector",
                ["source_id"] = "src1",
                ["stable_id"] = "send_email",
                ["schema_sha256"] = SchemaSha,
                ["risk"] = "medium"
            },
            ["arguments"] = new JsonObject { ["to"] = "a@b.c" }
        });
        var workflow = new WorkflowDefinition(1, "n1", new[] { agent, cap }, new[] { new WorkflowEdge("n1", "n2", "next") });
        var binding = new AutomationBinding(ProjectIdHex, ExpertIdHex);
        var budget = new RunBudget(2, 8192, 300, 3, 4096);
        var permission = new PermissionRequest(new[] { "send_email" }, "read-only");
        var revision = AutomationRevision.Create(revId, autoId, 1, trigger, workflow, binding, budget, OverlapPolicy.Skip, MissedRunPolicy.Skip, permission, Clock.UtcNow);
        var def = AutomationDefinition.Create(autoId, SpaceId.Parse(SpaceIdHex), "Sample", "a sample", revId, Clock.UtcNow).Value!;
        repo.Add(def, revision);
        return (autoId, revId);
    }

    private static JsonObject BuildEnvelope(string triggerType, string? timezone = "UTC", string? expertId = ExpertIdHex)
    {
        var trigger = new JsonObject
        {
            ["trigger_id"] = "trig1",
            ["type"] = triggerType,
            ["enabled"] = true
        };
        if (triggerType != "manual")
        {
            trigger["timezone_id"] = (JsonNode?)timezone;
            if (triggerType == "interval")
            {
                trigger["interval_seconds"] = 3600;
                trigger["anchor_at_utc"] = "2026-01-01T00:00:00Z";
            }
            else if (triggerType == "calendar_daily" || triggerType == "calendar_weekly")
            {
                trigger["local_time"] = "09:00";
                trigger["days_of_week"] = new JsonArray(1, 2, 3, 4, 5);
            }
        }

        var envelope = new JsonObject
        {
            ["schema_version"] = 1,
            ["name"] = "Sample",
            ["description"] = "a sample",
            ["binding"] = new JsonObject
            {
                ["space_id"] = SpaceIdHex,
                ["project_id"] = ProjectIdHex,
                ["expert_id"] = (JsonNode?)expertId,
                ["model_policy"] = "expert_revision"
            },
            ["trigger"] = trigger,
            ["workflow"] = new JsonObject
            {
                ["schema_version"] = 1,
                ["entry_node_id"] = "n1",
                ["nodes"] = new JsonArray(
                    new JsonObject
                    {
                        ["node_id"] = "n1",
                        ["kind"] = "agent_prompt",
                        ["display_name"] = "Agent",
                        ["timeout_seconds"] = 60,
                        ["disabled"] = false,
                        ["payload"] = new JsonObject { ["instruction_template"] = "x", ["output_key"] = "r" }
                    },
                    new JsonObject
                    {
                        ["node_id"] = "n2",
                        ["kind"] = "capability_call",
                        ["display_name"] = "Send",
                        ["timeout_seconds"] = 60,
                        ["disabled"] = false,
                        ["payload"] = new JsonObject
                        {
                            ["capability"] = new JsonObject
                            {
                                ["source_kind"] = "connector",
                                ["source_id"] = "src1",
                                ["stable_id"] = "send_email",
                                ["schema_sha256"] = SchemaSha
                            }
                        }
                    }),
                ["edges"] = new JsonArray(new JsonObject { ["from_node_id"] = "n1", ["to_node_id"] = "n2", ["branch"] = "next" })
            },
            ["budget"] = new JsonObject
            {
                ["wall_clock_seconds"] = 300,
                ["model_turns"] = 2,
                ["capability_calls"] = 3,
                ["result_bytes"] = 4096
            },
            ["overlap_policy"] = "skip",
            ["missed_run_policy"] = "skip",
            ["permission_request"] = new JsonObject
            {
                ["capabilities"] = new JsonArray(new JsonObject
                {
                    ["source_kind"] = "connector",
                    ["source_id"] = "src1",
                    ["capability_stable_id"] = "send_email",
                    ["schema_sha256"] = SchemaSha
                })
            }
        };
        return envelope;
    }
}

// ---- fakes -----------------------------------------------------------------

internal sealed class SequentialIdGenerator : IIdGenerator
{
    private long _counter;
    public string NewId()
    {
        var v = ++_counter;
        return v.ToString("00000000000000000000000000000000", System.Globalization.CultureInfo.InvariantCulture);
    }
}

internal sealed class FixedClock : IClock
{
    private static readonly DateTimeOffset Fixed = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public DateTimeOffset UtcNow => Fixed;
    public DateTimeOffset Now => Fixed;
}

internal sealed class RecordingAutomationRepository : IAutomationRepository
{
    public List<AutomationDefinition> Defs { get; } = new();
    public Dictionary<AutomationRevisionId, AutomationRevision> Revisions { get; } = new();

    public void Add(AutomationDefinition def, AutomationRevision rev)
    {
        Defs.Add(def);
        Revisions[rev.Id] = rev;
    }

    public Task<Result<AutomationDefinition>> GetAsync(AutomationId id, CancellationToken ct) =>
        Task.FromResult(Defs.FirstOrDefault(d => d.Id == id) is { } d ? Result<AutomationDefinition>.Ok(d)
            : Result<AutomationDefinition>.Fail(AutomationErrors.NotFoundError()));

    public Task<Result<IReadOnlyList<AutomationDefinition>>> ListBySpaceAsync(SpaceId spaceId, bool includeDeleted, CancellationToken ct) =>
        Task.FromResult(Result<IReadOnlyList<AutomationDefinition>>.Ok(
            Defs.Where(d => d.SpaceId == spaceId && (includeDeleted || d.Lifecycle != AutomationLifecycle.Deleted)).ToList()));

    public Task<Result<IReadOnlyList<AutomationRevision>>> GetRevisionsAsync(AutomationId id, CancellationToken ct) =>
        Task.FromResult(Result<IReadOnlyList<AutomationRevision>>.Ok(Revisions.Values.ToList()));

    public Task<Result<AutomationRevision>> GetRevisionAsync(AutomationRevisionId revisionId, CancellationToken ct) =>
        Task.FromResult(Revisions.TryGetValue(revisionId, out var r) ? Result<AutomationRevision>.Ok(r)
            : Result<AutomationRevision>.Fail(AutomationErrors.RevisionNotFoundError()));

    public Task<Result<AutomationDefinition>> SaveAsync(AutomationDefinition definition, AutomationRevision? newRevision, CancellationToken ct)
    {
        var i = Defs.FindIndex(d => d.Id == definition.Id);
        if (i >= 0) Defs[i] = definition; else Defs.Add(definition);
        if (newRevision is not null) Revisions[newRevision.Id] = newRevision;
        return Task.FromResult(Result<AutomationDefinition>.Ok(definition));
    }

    public Task<Result<IReadOnlyList<AutomationDefinition>>> ListEnabledAsync(CancellationToken ct) =>
        Task.FromResult(Result<IReadOnlyList<AutomationDefinition>>.Ok(
            Defs.Where(d => d.Lifecycle == AutomationLifecycle.Enabled).ToList()));
}
