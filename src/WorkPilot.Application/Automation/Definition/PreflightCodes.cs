using WorkPilot.Domain.Automation.Validation;

namespace WorkPilot.Application.Automation.Definition;

/// <summary>
/// Stable, catalog-style codes for the enable Preflight (AUT-005 / AUT-A10). Severity-bearing and
/// located by JSON Pointer so the editor can scroll to the exact node. Message keys follow the
/// <c>Preflight.&lt;CODE&gt;</c> convention. Codes are immutable once published.
/// </summary>
public static class PreflightCodes
{
    // ---- Binding / source ----
    public const string BindingSpaceMissing = "PREFLIGHT_BINDING_SPACE_MISSING";
    public const string BindingExpertMissing = "PREFLIGHT_BINDING_EXPERT_MISSING";

    // ---- Budget ----
    public const string BudgetWallInvalid = "PREFLIGHT_BUDGET_WALL_INVALID";
    public const string BudgetTurnsInvalid = "PREFLIGHT_BUDGET_TURNS_INVALID";
    public const string BudgetCapsInvalid = "PREFLIGHT_BUDGET_CAPS_INVALID";
    public const string BudgetBytesInvalid = "PREFLIGHT_BUDGET_BYTES_INVALID";
    public const string BudgetTokensInvalid = "PREFLIGHT_BUDGET_TOKENS_INVALID";

    // ---- Capability permission pre-check (T17) ----
    public const string CapabilityIdentityMissing = "PREFLIGHT_CAPABILITY_IDENTITY_MISSING";
    public const string CapabilityDenied = "PREFLIGHT_CAPABILITY_DENIED";
    public const string CapabilityAsk = "PREFLIGHT_CAPABILITY_ASK";
    public const string CapabilityDeferred = "PREFLIGHT_CAPABILITY_DEFERRED";

    public static ValidationIssue Issue(
        ValidationSeverity severity, string code, string jsonPointer,
        params (string Key, string Value)[] details) =>
        new(severity, code, jsonPointer, $"Preflight.{code}",
            details.Length == 0 ? null : details.ToDictionary(d => d.Key, d => d.Value));

    public static ValidationIssue Error(string code, string jsonPointer,
        params (string Key, string Value)[] details) =>
        Issue(ValidationSeverity.Error, code, jsonPointer, details);

    public static ValidationIssue Warning(string code, string jsonPointer,
        params (string Key, string Value)[] details) =>
        Issue(ValidationSeverity.Warning, code, jsonPointer, details);
}
