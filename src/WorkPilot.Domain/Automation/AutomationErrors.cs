using System.Collections.Generic;
using WorkPilot.Contracts.Primitives;

namespace WorkPilot.Domain.Automation;

/// <summary>
/// Versioned error catalog for the Automation feature (AUT-* codes). Registered globally so the
/// catalog enforces cross-feature code uniqueness (AI dev rule §13).
/// </summary>
public sealed class AutomationErrors : FeatureErrorCatalog
{
    public override string Feature => "Automation";

    public static readonly ErrorDefinition NameLength = new("AUT_NAME_LENGTH", ErrorCategory.Validation, "Automation.NameLength", false);
    public static readonly ErrorDefinition NameControlChars = new("AUT_NAME_CONTROL", ErrorCategory.Validation, "Automation.NameControlChars", false);
    public static readonly ErrorDefinition DescriptionLength = new("AUT_DESC_LENGTH", ErrorCategory.Validation, "Automation.DescriptionLength", false);
    public static readonly ErrorDefinition DescriptionControlChars = new("AUT_DESC_CONTROL", ErrorCategory.Validation, "Automation.DescriptionControlChars", false);
    public static readonly ErrorDefinition DeletedCannotModify = new("AUT_DELETED_MODIFY", ErrorCategory.Conflict, "Automation.DeletedCannotModify", false);
    public static readonly ErrorDefinition RevisionNotNewer = new("AUT_REVISION_NOT_NEWER", ErrorCategory.Conflict, "Automation.RevisionNotNewer", false);
    public static readonly ErrorDefinition InvalidTransition = new("AUT_INVALID_TRANSITION", ErrorCategory.Conflict, "Automation.InvalidTransition", false);
    public static readonly ErrorDefinition SpaceImmutable = new("AUT_SPACE_IMMUTABLE", ErrorCategory.Validation, "Automation.SpaceImmutable", false);
    public static readonly ErrorDefinition ConcurrencyConflict = new("AUT_CONCURRENCY", ErrorCategory.Conflict, "Automation.ConcurrencyConflict", false);
    public static readonly ErrorDefinition NotFound = new("AUT_NOT_FOUND", ErrorCategory.Resource, "Automation.NotFound", false);
    public static readonly ErrorDefinition RevisionNotFound = new("AUT_REVISION_NOT_FOUND", ErrorCategory.Resource, "Automation.RevisionNotFound", false);
    public static readonly ErrorDefinition CanonicalMismatch = new("AUT_CANONICAL_MISMATCH", ErrorCategory.Database, "Automation.CanonicalMismatch", false);

    // Definition import/export (T22, AUT-006 / AUT-A07 / AUT-A08).
    public static readonly ErrorDefinition DefinitionMalformed = new("AUT_DEF_MALFORMED", ErrorCategory.Validation, "Definition.Malformed", false);
    public static readonly ErrorDefinition DefinitionInvalidSchemaVersion = new("AUT_DEF_SCHEMA_VER", ErrorCategory.Validation, "Definition.InvalidSchemaVersion", false);
    public static readonly ErrorDefinition DefinitionContainsSecret = new("AUT_DEF_SECRET", ErrorCategory.Validation, "Definition.ContainsSecret", false);
    public static readonly ErrorDefinition DefinitionInvalidBinding = new("AUT_DEF_BINDING", ErrorCategory.Validation, "Definition.InvalidBinding", false);
    public static readonly ErrorDefinition DefinitionInvalidTrigger = new("AUT_DEF_TRIGGER", ErrorCategory.Validation, "Definition.InvalidTrigger", false);
    public static readonly ErrorDefinition DefinitionInvalidWorkflow = new("AUT_DEF_WORKFLOW", ErrorCategory.Validation, "Definition.InvalidWorkflow", false);
    public static readonly ErrorDefinition DefinitionInvalidBudget = new("AUT_DEF_BUDGET", ErrorCategory.Validation, "Definition.InvalidBudget", false);
    public static readonly ErrorDefinition DefinitionInvalidPermission = new("AUT_DEF_PERMISSION", ErrorCategory.Validation, "Definition.InvalidPermission", false);
    public static readonly ErrorDefinition DefinitionImportFailed = new("AUT_DEF_IMPORT", ErrorCategory.Internal, "Definition.ImportFailed", false);

    public override IReadOnlyList<ErrorDefinition> Definitions => new[]
    {
        NameLength, NameControlChars, DescriptionLength, DescriptionControlChars,
        DeletedCannotModify, RevisionNotNewer, InvalidTransition, SpaceImmutable,
        ConcurrencyConflict, NotFound, RevisionNotFound, CanonicalMismatch,
        DefinitionMalformed, DefinitionInvalidSchemaVersion, DefinitionContainsSecret,
        DefinitionInvalidBinding, DefinitionInvalidTrigger, DefinitionInvalidWorkflow,
        DefinitionInvalidBudget, DefinitionInvalidPermission, DefinitionImportFailed
    };

    public static readonly AutomationErrors Instance = new();

    static AutomationErrors() => ErrorCatalog.Register(Instance);

    public static AppError NameLengthError() => Instance.Error("AUT_NAME_LENGTH");
    public static AppError NameControlCharsError() => Instance.Error("AUT_NAME_CONTROL");
    public static AppError DescriptionLengthError() => Instance.Error("AUT_DESC_LENGTH");
    public static AppError DescriptionControlCharsError() => Instance.Error("AUT_DESC_CONTROL");
    public static AppError DeletedCannotModifyError() => Instance.Error("AUT_DELETED_MODIFY");
    public static AppError RevisionNotNewerError() => Instance.Error("AUT_REVISION_NOT_NEWER");
    public static AppError InvalidTransitionError(AutomationLifecycle from, AutomationLifecycle to)
        => Instance.Error("AUT_INVALID_TRANSITION", new Dictionary<string, string> { ["from"] = from.ToStorage(), ["to"] = to.ToStorage() });
    public static AppError SpaceImmutableError() => Instance.Error("AUT_SPACE_IMMUTABLE");
    public static AppError ConcurrencyConflictError() => Instance.Error("AUT_CONCURRENCY");
    public static AppError NotFoundError() => Instance.Error("AUT_NOT_FOUND");
    public static AppError RevisionNotFoundError() => Instance.Error("AUT_REVISION_NOT_FOUND");
    public static AppError CanonicalMismatchError() => Instance.Error("AUT_CANONICAL_MISMATCH");

    public static AppError DefinitionMalformedError(string detail)
        => Instance.Error("AUT_DEF_MALFORMED", new Dictionary<string, string> { ["detail"] = detail });
    public static AppError DefinitionInvalidSchemaVersionError(int version)
        => Instance.Error("AUT_DEF_SCHEMA_VER", new Dictionary<string, string> { ["version"] = version.ToString(System.Globalization.CultureInfo.InvariantCulture) });
    public static AppError DefinitionContainsSecretError(string key)
        => Instance.Error("AUT_DEF_SECRET", new Dictionary<string, string> { ["key"] = key });
    public static AppError DefinitionInvalidBindingError(string detail)
        => Instance.Error("AUT_DEF_BINDING", new Dictionary<string, string> { ["detail"] = detail });
    public static AppError DefinitionInvalidTriggerError(string detail)
        => Instance.Error("AUT_DEF_TRIGGER", new Dictionary<string, string> { ["detail"] = detail });
    public static AppError DefinitionInvalidWorkflowError(string detail)
        => Instance.Error("AUT_DEF_WORKFLOW", new Dictionary<string, string> { ["detail"] = detail });
    public static AppError DefinitionInvalidBudgetError(string detail)
        => Instance.Error("AUT_DEF_BUDGET", new Dictionary<string, string> { ["detail"] = detail });
    public static AppError DefinitionInvalidPermissionError(string detail)
        => Instance.Error("AUT_DEF_PERMISSION", new Dictionary<string, string> { ["detail"] = detail });
    public static AppError DefinitionImportFailedError(string detail)
        => Instance.Error("AUT_DEF_IMPORT", new Dictionary<string, string> { ["detail"] = detail });
}
