using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation;
using Xunit;

namespace WorkPilot.Domain.Tests;

public class AutomationAggregateTests
{
    private static readonly SpaceId Space = SpaceId.Parse("space_1");
    private static readonly AutomationRevisionId Rev1 = AutomationRevisionId.Parse("rev_1");
    private static readonly DateTimeOffset Now = Samples.FixedNow;

    private static Result<AutomationDefinition> NewDraft(string name = "Daily report") =>
        AutomationDefinition.Create(AutomationId.Parse("auto_1"), Space, name, "desc", Rev1, Now);

    [Fact]
    public void Create_valid_becomes_draft_with_revision_1()
    {
        var r = NewDraft();
        Assert.True(r.IsSuccess);
        var def = r.Value!;
        Assert.Equal(AutomationLifecycle.Draft, def.Lifecycle);
        Assert.Equal(1, def.RevisionNumber);
        Assert.Equal(Space, def.SpaceId); // AUT-002: space immutable, set at creation
        Assert.Equal(Rev1, def.CurrentRevisionId);
    }

    [Fact]
    public void Create_rejects_name_too_long()
    {
        var name = new string('a', Limits.V1_5.MaxAutomationNameLength + 1);
        var r = NewDraft(name);
        Assert.False(r.IsSuccess);
        Assert.Equal("AUT_NAME_LENGTH", r.Error!.Code);
    }

    [Fact]
    public void Create_rejects_name_with_control_char()
    {
        var r = NewDraft("bad\0name");
        Assert.False(r.IsSuccess);
        Assert.Equal("AUT_NAME_CONTROL", r.Error!.Code);
    }

    [Fact]
    public void Create_rejects_empty_name()
    {
        var r = NewDraft("   ");
        Assert.False(r.IsSuccess);
        Assert.Equal("AUT_NAME_LENGTH", r.Error!.Code);
    }

    [Fact]
    public void Rename_fails_once_deleted()
    {
        var def = NewDraft().Value!;
        Assert.True(def.SoftDelete().IsSuccess);
        var rename = def.Rename("new name");
        Assert.False(rename.IsSuccess);
        Assert.Equal("AUT_DELETED_MODIFY", rename.Error!.Code);
    }

    [Fact]
    public void Publish_rejects_older_revision_but_accepts_current()
    {
        var def = NewDraft().Value!;
        // publishing the current revision (1) is allowed: Draft -> Enabled
        var current = def.Publish(AutomationRevisionId.Parse("rev_1"), 1);
        Assert.True(current.IsSuccess);
        Assert.Equal(AutomationLifecycle.Enabled, def.Lifecycle);

        // after promoting to revision 2, publishing the now-older revision 1 is rejected
        var def2 = NewDraft().Value!;
        Assert.True(def2.PromoteDraftRevision(AutomationRevisionId.Parse("rev_2"), 2).IsSuccess);
        var older = def2.Publish(AutomationRevisionId.Parse("rev_1"), 1);
        Assert.False(older.IsSuccess);
        Assert.Equal("AUT_REVISION_NOT_NEWER", older.Error!.Code);
    }

    [Fact]
    public void Archive_only_from_non_deleted()
    {
        var def = NewDraft().Value!;
        Assert.True(def.Archive().IsSuccess);
        Assert.Equal(AutomationLifecycle.Archived, def.Lifecycle);
    }

    [Fact]
    public void Pause_resume_transitions_are_guarded()
    {
        var def = NewDraft().Value!;
        Assert.False(def.Pause().IsSuccess); // not enabled yet

        Assert.True(def.Publish(AutomationRevisionId.Parse("rev_2"), 2).IsSuccess);
        Assert.True(def.Pause().IsSuccess);
        Assert.Equal(AutomationLifecycle.Paused, def.Lifecycle);
        Assert.True(def.Resume().IsSuccess);
        Assert.Equal(AutomationLifecycle.Enabled, def.Lifecycle);
    }

    [Fact]
    public void SoftDelete_is_idempotent()
    {
        var def = NewDraft().Value!;
        Assert.True(def.SoftDelete().IsSuccess);
        Assert.Equal(AutomationLifecycle.Deleted, def.Lifecycle);
        var again = def.SoftDelete();
        Assert.True(again.IsSuccess);
    }
}
