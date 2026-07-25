namespace WorkPilot.Domain.PermissionGovernance;

/// <summary>
/// Effective risk of a capability invocation. Mirrors <c>WorkPilot.Models.RiskLevel</c> (WinUI) so
/// the App layer can map without ambiguity; defined here in the shared domain so the Policy Core
/// (T17 evaluator) never depends on the WinUI project. Values are fixed (Low=0..Critical=3).
/// </summary>
public enum RiskLevel : int
{
    Low = 0,
    Medium = 1,
    High = 2,
    Critical = 3
}

/// <summary>Policy statement effect. Deny always wins regardless of layer (doc 07 §3/§6).</summary>
public enum PolicyEffect : int
{
    Allow = 0,
    Ask = 1,
    Deny = 2
}

/// <summary>Subject a statement applies to (doc 07 §3).</summary>
public enum PolicySubject : int
{
    InteractiveUser = 0,
    AutomationPrincipal = 1,
    SystemMaintenance = 2
}

/// <summary>
/// Policy layers, ordered by constraint from outside in (doc 07 §3). BuiltInSafety ships with the
/// product and is non-editable; the remaining layers are per-scope documents.
/// </summary>
public enum PolicyLayer : int
{
    BuiltInSafety = 0,
    GlobalPolicy = 1,
    SpacePolicy = 2,
    ExpertPolicy = 3,
    AutomationPolicy = 4
}
