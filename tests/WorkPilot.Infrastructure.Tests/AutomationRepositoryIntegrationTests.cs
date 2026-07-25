using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using WorkPilot.Application.Automation;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation;
using WorkPilot.Infrastructure.Automation;
using WorkPilot.Infrastructure.Data;
using Xunit;

namespace WorkPilot.Infrastructure.Tests;

public class AutomationRepositoryIntegrationTests
{
    private static readonly SpaceId Space = SpaceId.Parse("space_1");
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class TestIdGenerator : IIdGenerator
    {
        private int _c;
        public string NewId() => $"tid_{++_c:000000}";
    }

    private static async Task<(SqliteConnection conn, AutomationService svc, AutomationRepository repo)> BootstrapAsync()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();

        // spaces is a pre-existing V1.4 table (created by an earlier migration in production); the
        // 017 schema FKs automation_definitions/domain_event_outbox to it, so a fresh test DB must
        // provide it. Kept out of CreateTablesAsync to preserve its "no drift from Migration017Ddl" guarantee.
        var createSpaces = conn.CreateCommand();
        createSpaces.CommandText = "CREATE TABLE spaces(id TEXT PRIMARY KEY, is_default INTEGER NOT NULL DEFAULT 0)";
        await createSpaces.ExecuteNonQueryAsync();
        foreach (var sid in new[] { Space.Value, "space_2" })
        {
            var seedSpaces = conn.CreateCommand();
            seedSpaces.CommandText = "INSERT INTO spaces(id,is_default) VALUES($id,1)";
            seedSpaces.Parameters.AddWithValue("$id", sid);
            await seedSpaces.ExecuteNonQueryAsync();
        }

        var migrator = new V15DatabaseMigrator(new FakeClock(Now));
        await migrator.CreateTablesAsync(conn);
        var repo = new AutomationRepository(conn);
        var svc = new AutomationService(repo, new TestIdGenerator(), new FakeClock(Now));
        return (conn, svc, repo);
    }

    private static CreateAutomationRequest SampleRequest(string name = "Daily report") => new(
        Space, name, "summarize", BuildTrigger(), BuildWorkflow(), new AutomationBinding(null, null),
        new RunBudget(8, 200_000, 3600, 100, 10_000_000), OverlapPolicy.Skip, MissedRunPolicy.RunOnce, new PermissionRequest(Array.Empty<string>(), "read-only"));

    private static TriggerDefinition BuildTrigger() => new("interval_1", TriggerType.Interval, true, null,
        null, null, 3600, Now, null, null, null, null, null, null);

    private static WorkflowDefinition BuildWorkflow() => new(1, "agent_prompt_1",
        new[] { new WorkflowNode("agent_prompt_1", "指令", "agent_prompt", 60, false, null) }, Array.Empty<WorkflowEdge>());

    [Fact]
    public async Task Create_then_get_round_trips_as_draft_with_revision_1()
    {
        var (conn, svc, _) = await BootstrapAsync();
        await using var _ = conn;
        var created = await svc.CreateAsync(SampleRequest(), CancellationToken.None);
        Assert.True(created.IsSuccess);
        Assert.Equal(AutomationLifecycle.Draft, created.Value!.Lifecycle);
        Assert.Equal(1, created.Value.RevisionNumber);

        var got = await svc.GetAsync(created.Value.Id, CancellationToken.None);
        Assert.True(got.IsSuccess);
        Assert.Equal("Daily report", got.Value!.Name);
        Assert.Equal(created.Value.CurrentRevisionId, got.Value.CurrentRevisionId);
    }

    [Fact]
    public async Task Editing_draft_produces_a_new_immutable_revision()
    {
        var (conn, svc, repo) = await BootstrapAsync();
        await using var _ = conn;
        var created = (await svc.CreateAsync(SampleRequest(), CancellationToken.None)).Value!;
        var originalRevision = created.CurrentRevisionId;

        var update = await svc.UpdateDraftAsync(new UpdateAutomationRequest(
            created.Id, "Daily report v2", "new desc", created.RowVersion,
            Trigger: null, Workflow: null, Binding: null,
            Budget: new RunBudget(12, 300_000, 3600, 100, 10_000_000), OverlapPolicy: null, MissedRunPolicy: null, Permission: null),
            CancellationToken.None);
        Assert.True(update.IsSuccess);
        Assert.Equal(2, update.Value!.RevisionNumber);

        var revisions = (await repo.GetRevisionsAsync(created.Id, CancellationToken.None)).Value!;
        Assert.Equal(2, revisions.Count); // old revision retained, immutable (AUT-001)

        // original revision content is unchanged
        var original = (await repo.GetRevisionAsync(originalRevision, CancellationToken.None)).Value!;
        Assert.Equal(1, original.RevisionNumber);
        Assert.Equal(200_000, original.Budget.MaxTotalTokens);
    }

    [Fact]
    public async Task Publish_enables_and_points_at_revision()
    {
        var (conn, svc, _) = await BootstrapAsync();
        await using var _ = conn;
        var created = (await svc.CreateAsync(SampleRequest(), CancellationToken.None)).Value!;
        var rev2 = (await svc.UpdateDraftAsync(new UpdateAutomationRequest(
            created.Id, created.Name, created.Description, created.RowVersion,
            null, null, null, new RunBudget(12, 300_000, 3600, 100, 10_000_000), null, null, null), CancellationToken.None)).Value!;

        var published = await svc.PublishAsync(created.Id, rev2.CurrentRevisionId, rev2.RowVersion, CancellationToken.None);
        Assert.True(published.IsSuccess);
        Assert.Equal(AutomationLifecycle.Enabled, published.Value!.Lifecycle);
        Assert.Equal(rev2.CurrentRevisionId, published.Value!.CurrentRevisionId);
    }

    [Fact]
    public async Task SoftDelete_excludes_from_default_list_but_keeps_identity()
    {
        var (conn, svc, _) = await BootstrapAsync();
        await using var _ = conn;
        var created = (await svc.CreateAsync(SampleRequest(), CancellationToken.None)).Value!;

        var deleted = await svc.SoftDeleteAsync(created.Id, created.RowVersion, CancellationToken.None);
        Assert.True(deleted.IsSuccess);
        Assert.Equal(AutomationLifecycle.Deleted, deleted.Value!.Lifecycle);

        var visible = (await svc.ListBySpaceAsync(Space, includeDeleted: false, CancellationToken.None)).Value!;
        Assert.DoesNotContain(visible, d => d.Id == created.Id);

        var includingDeleted = (await svc.ListBySpaceAsync(Space, includeDeleted: true, CancellationToken.None)).Value!;
        Assert.Contains(includingDeleted, d => d.Id == created.Id);

        // identity is retained for historical run references
        var got = await svc.GetAsync(created.Id, CancellationToken.None);
        Assert.True(got.IsSuccess);
    }

    [Fact]
    public async Task Copy_produces_new_draft_in_target_space()
    {
        var (conn, svc, _) = await BootstrapAsync();
        await using var _ = conn;
        var created = (await svc.CreateAsync(SampleRequest(), CancellationToken.None)).Value!;
        var target = SpaceId.Parse("space_2");

        var copy = await svc.CopyAsync(created.Id, target, CancellationToken.None);
        Assert.True(copy.IsSuccess);
        Assert.Equal(target, copy.Value!.SpaceId);
        Assert.Equal(AutomationLifecycle.Draft, copy.Value!.Lifecycle);
        Assert.Equal(1, copy.Value!.RevisionNumber);
        Assert.EndsWith(" (copy)", copy.Value!.Name);
        Assert.NotEqual(created.Id, copy.Value!.Id);
    }

    [Fact]
    public async Task Stale_row_version_is_rejected_as_concurrency_conflict()
    {
        var (conn, svc, _) = await BootstrapAsync();
        await using var _ = conn;
        var created = (await svc.CreateAsync(SampleRequest(), CancellationToken.None)).Value!;
        var rev2 = (await svc.UpdateDraftAsync(new UpdateAutomationRequest(
            created.Id, created.Name, created.Description, created.RowVersion,
            null, null, null, new RunBudget(12, 300_000, 3600, 100, 10_000_000), null, null, null), CancellationToken.None)).Value!;

        // caller holds a stale expectedRowVersion (simulates concurrent edit, AUT-008)
        var conflict = await svc.PublishAsync(created.Id, rev2.CurrentRevisionId, expectedRowVersion: 1, CancellationToken.None);
        Assert.False(conflict.IsSuccess);
        Assert.Equal("AUT_CONCURRENCY", conflict.Error!.Code);
    }

    [Fact]
    public async Task AnalyzeImpact_reports_revision_history_and_diff()
    {
        var (conn, svc, _) = await BootstrapAsync();
        await using var _ = conn;
        var created = (await svc.CreateAsync(SampleRequest(), CancellationToken.None)).Value!;
        await svc.UpdateDraftAsync(new UpdateAutomationRequest(
            created.Id, created.Name, created.Description, created.RowVersion,
            null, null, null, new RunBudget(12, 300_000, 3600, 100, 10_000_000), null, null, null), CancellationToken.None);

        var impact = (await svc.AnalyzeImpactAsync(created.Id, CancellationToken.None)).Value!;
        Assert.Equal(2, impact.RevisionCount);
        Assert.False(impact.RunReferenceCheckAvailable); // run tables land in T07
        Assert.True(impact.HasUnpublishedChanges);
        Assert.NotEmpty(impact.RecentDiffs);
    }
}
