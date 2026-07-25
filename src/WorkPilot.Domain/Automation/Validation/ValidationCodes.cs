namespace WorkPilot.Domain.Automation.Validation;

/// <summary>
/// Stable, catalog-style codes for validation findings. These are structural validation codes
/// (severity-bearing, located by JSON Pointer) distinct from runtime <see cref="AppError"/>s, so
/// they live here as domain constants rather than in the runtime <c>ErrorCatalog</c>. Codes are
/// immutable once published; do not rename meanings. Message keys follow the
/// <c>Validation.&lt;CODE&gt;</c> convention.
/// </summary>
public static class ValidationCodes
{
    // ---- Workflow structure ----
    public const string WorkflowEmpty = "WORKFLOW_EMPTY";
    public const string NodeCountExceeded = "NODE_COUNT_EXCEEDED";
    public const string EdgeCountExceeded = "EDGE_COUNT_EXCEEDED";
    public const string NodeIdInvalid = "NODE_ID_INVALID";
    public const string NodeIdDuplicate = "NODE_ID_DUPLICATE";
    public const string NodeDisplayNameInvalid = "NODE_DISPLAY_NAME_INVALID";
    public const string NodeTimeoutInvalid = "NODE_TIMEOUT_INVALID";
    public const string NodeKindInvalid = "NODE_KIND_INVALID";
    public const string EntryNotFound = "ENTRY_NOT_FOUND";
    public const string EntryInDegreeNonZero = "ENTRY_IN_DEGREE_NONZERO";
    public const string WorkflowCycle = "WORKFLOW_CYCLE";
    public const string WorkflowUnreachable = "WORKFLOW_UNREACHABLE";
    public const string NodeOutDegreeInvalid = "NODE_OUTDEGREE_INVALID";
    public const string ConditionBranchInvalid = "CONDITION_BRANCH_INVALID";
    public const string WorkflowNoTerminal = "WORKFLOW_NO_TERMINAL";
    public const string RetryPolicyInvalid = "RETRY_POLICY_INVALID";

    // ---- Variables ----
    public const string VariableOutputKeyInvalid = "VARIABLE_OUTPUT_KEY_INVALID";
    public const string VariableNameInvalid = "VARIABLE_NAME_INVALID";
    public const string VariableNotAvailable = "VARIABLE_NOT_AVAILABLE";

    // ---- Trigger ----
    public const string TriggerTypeInvalid = "TRIGGER_TYPE_INVALID";
    public const string IntervalSecondsInvalid = "INTERVAL_SECONDS_INVALID";
    public const string IntervalAnchorMissing = "INTERVAL_ANCHOR_MISSING";
    public const string CalendarLocalTimeInvalid = "CALENDAR_LOCAL_TIME_INVALID";
    public const string CalendarDaysOfWeekInvalid = "CALENDAR_DAYS_OF_WEEK_INVALID";
    public const string MonthlyDayInvalid = "MONTHLY_DAY_INVALID";
    public const string MonthlyMissingDayInvalid = "MONTHLY_MISSING_DAY_INVALID";
    public const string OnceFieldsMissing = "ONCE_FIELDS_MISSING";
    public const string DomainEventTypeInvalid = "DOMAIN_EVENT_TYPE_INVALID";
    public const string DomainEventFiltersInvalid = "DOMAIN_EVENT_FILTERS_INVALID";

    public static ValidationIssue Issue(
        ValidationSeverity severity,
        string code,
        string jsonPointer,
        params (string Key, string Value)[] details) =>
        new(severity, code, jsonPointer, $"Validation.{code}",
            details.Length == 0 ? null : details.ToDictionary(d => d.Key, d => d.Value));

    public static ValidationIssue Error(string code, string jsonPointer,
        params (string Key, string Value)[] details) =>
        Issue(ValidationSeverity.Error, code, jsonPointer, details);

    public static ValidationIssue Warning(string code, string jsonPointer,
        params (string Key, string Value)[] details) =>
        Issue(ValidationSeverity.Warning, code, jsonPointer, details);
}
