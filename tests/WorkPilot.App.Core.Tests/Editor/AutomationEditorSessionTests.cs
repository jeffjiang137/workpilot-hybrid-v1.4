using System;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.App.Core.Automation;
using WorkPilot.App.Core.Tests.Fakes;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation;
using WorkPilot.Domain.Automation.Scheduling;
using Xunit;

namespace WorkPilot.App.Core.Tests.Editor;

public class AutomationEditorSessionTests
{
    private static (AutomationEditorSession, StubAutomationService, StubClock, StubTimeZoneResolver) NewSut(DateTimeOffset? now = null)
    {
        var clock = new StubClock { UtcNow = now ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) };
        var ids = new DeterministicIdGenerator("t");
        var svc = new StubAutomationService(ids, clock);
        var zone = new ConfigurableZone(TimeSpan.Zero);
        var resolver = new StubTimeZoneResolver(zone);
        var session = new AutomationEditorSession(svc, clock, resolver);
        return (session, svc, clock, resolver);
    }

    private static SpaceId Space() => SpaceId.Parse("space-1");

    [Fact]
    public void LoadNew_is_not_dirty_and_starts_at_basic_info()
    {
        var (s, _, _, _) = NewSut();
        s.LoadNew(Space());
        Assert.False(s.IsDirty);
        Assert.Equal(EditorMode.New, s.Mode);
        Assert.Equal(EditorStep.BasicInfo, s.CurrentStep);
        Assert.True(s.CanMoveNext);
        Assert.False(s.CanMoveBack);
    }

    [Fact]
    public void Editing_after_load_marks_dirty()
    {
        var (s, _, _, _) = NewSut();
        s.LoadNew(Space());
        Assert.False(s.IsDirty);
        s.Name = "My automation";
        Assert.True(s.IsDirty);
    }

    [Fact]
    public void Reverting_edit_clears_dirty()
    {
        var (s, _, _, _) = NewSut();
        s.LoadNew(Space());
        s.Name = "Temp";
        Assert.True(s.IsDirty);
        s.Name = string.Empty; // back to empty (the seeded baseline)
        Assert.False(s.IsDirty);
    }

    [Fact]
    public async Task SaveDraft_new_creates_automation_and_resets_dirty()
    {
        var (s, svc, _, _) = NewSut();
        s.LoadNew(Space());
        s.Name = "New one";
        s.ExpertId = "expert-9";

        var result = await s.SaveDraftAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.False(result.Conflict);
        Assert.NotNull(s.Id);
        Assert.Equal(SaveState.Saved, s.State);
        Assert.False(s.IsDirty);
        Assert.Equal(1, svc.StoredRowVersion(s.Id!.Value));
    }

    [Fact]
    public async Task SaveDraft_edit_of_draft_automation_round_trips()
    {
        var (s, svc, clock, resolver) = NewSut();
        s.LoadNew(Space());
        s.Name = "Editable";
        s.ExpertId = "expert-9";
        var created = await s.SaveDraftAsync(CancellationToken.None);
        Assert.True(created.Succeeded);

        // Reload as if opening the editor on the saved draft (must reuse the SAME service/clock/resolver).
        var s2 = new AutomationEditorSession(svc, clock, resolver);
        await s2.LoadExistingAsync(s.Id!.Value, CancellationToken.None);
        Assert.Equal("Editable", s2.Name);
        Assert.False(s2.IsDirty);

        s2.Name = "Editable v2";
        Assert.True(s2.IsDirty);
        var updated = await s2.SaveDraftAsync(CancellationToken.None);
        Assert.True(updated.Succeeded);
        Assert.Equal(2, svc.StoredRowVersion(s.Id.Value));
    }

    [Fact]
    public async Task Concurrent_edit_produces_conflict_and_blocks_enable()
    {
        var (s, svc, _, _) = NewSut();
        s.LoadNew(Space());
        s.Name = "Conflictee";
        s.ExpertId = "expert-9";
        await s.SaveDraftAsync(CancellationToken.None);

        // Another user saves while we have the editor open.
        svc.SimulateExternalEdit(s.Id!.Value);

        s.Name = "My local edit";
        var result = await s.SaveDraftAsync(CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.True(result.Conflict);
        Assert.True(s.HasConflict);
        Assert.Equal(SaveState.Conflict, s.State);
        Assert.False(s.CanSaveAndEnable);
    }

    [Fact]
    public async Task ReloadFromServer_resolves_conflict()
    {
        var (s, svc, _, _) = NewSut();
        s.LoadNew(Space());
        s.Name = "Conflict target";
        s.ExpertId = "expert-9";
        await s.SaveDraftAsync(CancellationToken.None);
        svc.SimulateExternalEdit(s.Id!.Value);
        await s.SaveDraftAsync(CancellationToken.None);
        Assert.True(s.HasConflict);

        await s.ReloadFromServerAsync(CancellationToken.None);
        Assert.False(s.HasConflict);
        Assert.False(s.IsDirty);
    }

    [Fact]
    public async Task Space_change_after_load_forces_save_as_copy()
    {
        var (s, svc, clock, resolver) = NewSut();
        s.LoadNew(Space());
        s.Name = "Copy me";
        s.ExpertId = "expert-9";
        await s.SaveDraftAsync(CancellationToken.None);

        // Open the saved draft and change its space (must reuse the SAME service/clock/resolver).
        var s2 = new AutomationEditorSession(svc, clock, resolver);
        await s2.LoadExistingAsync(s.Id!.Value, CancellationToken.None);
        Assert.False(s2.SpaceChangedAfterLoad);
        s2.SpaceId = SpaceId.Parse("space-2");
        Assert.True(s2.SpaceChangedAfterLoad);

        var result = await s2.SaveDraftAsync(CancellationToken.None);
        Assert.True(result.Succeeded);
        // A brand-new automation was created in the new space; the original is untouched.
        Assert.Equal(1, svc.StoredRowVersion(s.Id.Value)); // original unchanged
        Assert.NotEqual(s.Id.Value, s2.Id!.Value);          // a new id was allocated
    }

    [Fact]
    public async Task SaveAndEnable_publishes_when_no_errors()
    {
        var (s, svc, _, _) = NewSut();
        s.LoadNew(Space());
        s.Name = "Enable me";
        s.ExpertId = "expert-9";

        var result = await s.SaveAndEnableAsync(CancellationToken.None);
        Assert.True(result.Succeeded);
        // After save-and-enable the automation exists and is enabled.
        var get = await svc.GetAsync(s.Id!.Value, CancellationToken.None);
        Assert.True(get.IsSuccess);
        Assert.Equal(AutomationLifecycle.Enabled, get.Value!.Lifecycle);
    }

    [Fact]
    public async Task SaveAndEnable_blocked_while_blocking_errors_present()
    {
        var (s, svc, _, _) = NewSut();
        s.LoadNew(Space());
        s.Name = "No expert"; // expert not set -> definition error
        Assert.True(s.HasBlockingErrors);
        Assert.False(s.CanSaveAndEnable);

        var result = await s.SaveAndEnableAsync(CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Equal(SaveState.Error, s.State);
    }

    [Fact]
    public void Step_navigation_bounds_enforced()
    {
        var (s, _, _, _) = NewSut();
        s.LoadNew(Space());
        for (var i = 0; i < 10; i++) s.GoNext();
        Assert.Equal(EditorStep.TestEnable, s.CurrentStep);
        Assert.False(s.CanMoveNext);
        for (var i = 0; i < 10; i++) s.GoBack();
        Assert.Equal(EditorStep.BasicInfo, s.CurrentStep);
        Assert.False(s.CanMoveBack);
    }

    [Fact]
    public void PreviewTrigger_uses_shared_algorithm_for_interval_cadence()
    {
        var (s, _, clock, _) = NewSut(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        s.LoadNew(Space());
        s.TriggerSession.ChangeType(TriggerType.Interval, clock.UtcNow);
        s.TriggerSession.IntervalSeconds = 3600; // 1h
        s.PreviewTrigger();

        Assert.Equal(10, s.TriggerPreview.Count);
        for (var i = 1; i < s.TriggerPreview.Count; i++)
        {
            var delta = (s.TriggerPreview[i].Utc - s.TriggerPreview[i - 1].Utc).TotalSeconds;
            Assert.Equal(3600, delta); // drift-free, exactly one interval apart
        }
    }

    [Fact]
    public void Manual_trigger_has_no_background_schedule_preview()
    {
        var (s, _, _, _) = NewSut();
        s.LoadNew(Space());
        s.TriggerSession.ChangeType(TriggerType.Manual);
        s.PreviewTrigger();
        Assert.Empty(s.TriggerPreview);
    }
}
