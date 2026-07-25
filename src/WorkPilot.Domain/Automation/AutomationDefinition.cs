using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;

namespace WorkPilot.Domain.Automation;

/// <summary>
/// Mutable automation aggregate root. Holds identity, the current revision pointer, lifecycle and
/// optimistic-concurrency version. Editing produces a NEW immutable revision (AUT-001); runs keep
/// referencing the old revision id so they never drift. <see cref="SpaceId"/> is immutable after
/// creation (AUT-002).
/// </summary>
public sealed class AutomationDefinition
{
    public AutomationId Id { get; }
    public SpaceId SpaceId { get; } // immutable after creation (AUT-002)
    public string Name { get; private set; }
    public string Description { get; private set; }
    public AutomationLifecycle Lifecycle { get; private set; }
    public AutomationRevisionId CurrentRevisionId { get; private set; }
    public int RevisionNumber { get; private set; }
    public long RowVersion { get; set; } // optimistic concurrency (AUT-008); managed by persistence
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public AutomationDefinition(
        AutomationId id, SpaceId spaceId, string name, string description,
        AutomationLifecycle lifecycle, AutomationRevisionId currentRevisionId,
        int revisionNumber, long rowVersion, DateTimeOffset createdAtUtc, DateTimeOffset updatedAtUtc)
    {
        Id = id;
        SpaceId = spaceId;
        Name = name;
        Description = description;
        Lifecycle = lifecycle;
        CurrentRevisionId = currentRevisionId;
        RevisionNumber = revisionNumber;
        RowVersion = rowVersion;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
    }

    public static Result<AutomationDefinition> Create(
        AutomationId id, SpaceId spaceId, string name, string description,
        AutomationRevisionId initialRevisionId, DateTimeOffset now)
    {
        if (spaceId == default)
            return Result<AutomationDefinition>.Fail(AutomationErrors.SpaceImmutableError());
        string validName;
        try { validName = ValidateName(name); }
        catch (DomainException ex) { return Result<AutomationDefinition>.Fail(ex.Error); }
        string validDesc;
        try { validDesc = ValidateDescription(description); }
        catch (DomainException ex) { return Result<AutomationDefinition>.Fail(ex.Error); }

        var def = new AutomationDefinition(id, spaceId, validName, validDesc,
            AutomationLifecycle.Draft, initialRevisionId, 1, 0, now, now);
        return Result<AutomationDefinition>.Ok(def);
    }

    public Result Rename(string name)
    {
        if (Lifecycle == AutomationLifecycle.Deleted)
            return Result.Failure(AutomationErrors.DeletedCannotModifyError());
        try { Name = ValidateName(name); }
        catch (DomainException ex) { return Result.Failure(ex.Error); }
        return Result.Success();
    }

    public Result ChangeDescription(string description)
    {
        if (Lifecycle == AutomationLifecycle.Deleted)
            return Result.Failure(AutomationErrors.DeletedCannotModifyError());
        try { Description = ValidateDescription(description); }
        catch (DomainException ex) { return Result.Failure(ex.Error); }
        return Result.Success();
    }

    /// <summary>Publish a strictly newer immutable revision as the current one (AUT-001).</summary>
    public Result Publish(AutomationRevisionId revisionId, int revisionNumber)
    {
        if (Lifecycle == AutomationLifecycle.Deleted)
            return Result.Failure(AutomationErrors.DeletedCannotModifyError());
        if (revisionNumber < RevisionNumber)
            return Result.Failure(AutomationErrors.RevisionNotNewerError());
        CurrentRevisionId = revisionId;
        RevisionNumber = revisionNumber;
        Lifecycle = AutomationLifecycle.Enabled;
        return Result.Success();
    }

    public Result Archive()
    {
        if (Lifecycle == AutomationLifecycle.Deleted)
            return Result.Failure(AutomationErrors.DeletedCannotModifyError());
        Lifecycle = AutomationLifecycle.Archived;
        return Result.Success();
    }

    public Result Pause()
    {
        if (Lifecycle == AutomationLifecycle.Deleted)
            return Result.Failure(AutomationErrors.DeletedCannotModifyError());
        if (Lifecycle != AutomationLifecycle.Enabled)
            return Result.Failure(AutomationErrors.InvalidTransitionError(Lifecycle, AutomationLifecycle.Paused));
        Lifecycle = AutomationLifecycle.Paused;
        return Result.Success();
    }

    public Result Resume()
    {
        if (Lifecycle == AutomationLifecycle.Deleted)
            return Result.Failure(AutomationErrors.DeletedCannotModifyError());
        if (Lifecycle != AutomationLifecycle.Paused)
            return Result.Failure(AutomationErrors.InvalidTransitionError(Lifecycle, AutomationLifecycle.Enabled));
        Lifecycle = AutomationLifecycle.Enabled;
        return Result.Success();
    }

    public Result SoftDelete()
    {
        if (Lifecycle == AutomationLifecycle.Deleted)
            return Result.Success();
        Lifecycle = AutomationLifecycle.Deleted;
        return Result.Success();
    }

    /// <summary>Point a draft at a freshly created immutable revision (used by UpdateDraftAsync).</summary>
    public Result PromoteDraftRevision(AutomationRevisionId revisionId, int revisionNumber)
    {
        if (Lifecycle != AutomationLifecycle.Draft)
            return Result.Failure(AutomationErrors.InvalidTransitionError(Lifecycle, AutomationLifecycle.Draft));
        if (revisionNumber <= RevisionNumber)
            return Result.Failure(AutomationErrors.RevisionNotNewerError());
        CurrentRevisionId = revisionId;
        RevisionNumber = revisionNumber;
        return Result.Success();
    }

    public void Touch(DateTimeOffset now) => UpdatedAtUtc = now;

    internal static string ValidateName(string name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length < 1 || trimmed.Length > Limits.V1_5.MaxAutomationNameLength)
            throw new DomainException(AutomationErrors.NameLengthError());
        if (ContainsIllegalControlChars(trimmed))
            throw new DomainException(AutomationErrors.NameControlCharsError());
        return trimmed;
    }

    internal static string ValidateDescription(string description)
    {
        var value = description ?? string.Empty;
        if (value.Length > Limits.V1_5.MaxAutomationDescriptionLength)
            throw new DomainException(AutomationErrors.DescriptionLengthError());
        if (ContainsIllegalControlChars(value))
            throw new DomainException(AutomationErrors.DescriptionControlCharsError());
        return value;
    }

    private static bool ContainsIllegalControlChars(string value)
    {
        foreach (var c in value)
        {
            if (c == '\t' || c == '\n') continue;
            if (char.IsControl(c)) return true;
        }
        return false;
    }
}
