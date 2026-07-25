using System.Text.Json;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;

namespace WorkPilot.Domain.PermissionGovernance;

/// <summary>
/// A single policy rule within a version (doc 07 §3). Statements are immutable once a version is
/// saved — editing produces a new version with new statements (PER-009). <c>*</c> is forbidden as a
/// permanent Allow selector for any source/capability (doc 07 §3); the <see cref="HasWildcardAllow"/>
/// check enforces this and the default-policy provider never emits such a statement.
/// </summary>
public sealed record PolicyStatement(
    PolicyStatementId Id,
    PolicyVersionId VersionId,
    bool Enabled,
    PolicyEffect Effect,
    IReadOnlyList<PolicySubject> Subjects,
    string SourceSelectorJson,
    string CapabilitySelectorJson,
    RiskLevel RiskMin,
    RiskLevel RiskMax,
    ResourceScope? Scope,
    IReadOnlyList<PolicyCondition> Conditions,
    int Priority)
{
    public const string Wildcard = "*";

    /// <summary>True if this is an Allow with a <c>*</c> selector for source or capability — always invalid.</summary>
    public bool HasWildcardAllow()
    {
        if (Effect != PolicyEffect.Allow)
            return false;
        return SelectorHasWildcard(SourceSelectorJson) || SelectorHasWildcard(CapabilitySelectorJson);
    }

    private static bool SelectorHasWildcard(string selectorJson)
    {
        if (string.IsNullOrWhiteSpace(selectorJson))
            return false;
        // Wildcard appears as a bare "*" string value on "source" / "capability" keys.
        return selectorJson.Contains("\"source\"") && selectorJson.Contains("\"*\"")
            || selectorJson.Contains("\"capability\"") && selectorJson.Contains("\"*\"");
    }

    /// <summary>Validates structural invariants. Does not evaluate the policy (that is T17).</summary>
    public Result Validate()
    {
        if (Conditions.Count > Limits.V1_5.MaxPolicyConditionsPerStatement)
            return Result.Failure(PolicyErrors.ConditionCountError(Conditions.Count));
        foreach (var c in Conditions)
            if (!c.IsValid())
                return Result.Failure(PolicyErrors.ConditionInvalidError());
        if (RiskMin > RiskMax)
            return Result.Failure(PolicyErrors.RiskRangeError());
        if (HasWildcardAllow())
            return Result.Failure(PolicyErrors.WildcardAllowError());
        return Result.Success();
    }

    public static PolicyStatement Create(
        IIdGenerator ids,
        PolicyVersionId versionId,
        bool enabled,
        PolicyEffect effect,
        IReadOnlyList<PolicySubject> subjects,
        string sourceSelectorJson,
        string capabilitySelectorJson,
        RiskLevel riskMin,
        RiskLevel riskMax,
        ResourceScope? scope,
        IReadOnlyList<PolicyCondition> conditions,
        int priority)
    {
        var statement = new PolicyStatement(
            PolicyStatementId.Create(ids), versionId, enabled, effect, subjects,
            sourceSelectorJson, capabilitySelectorJson, riskMin, riskMax, scope, conditions, priority);
        var validation = statement.Validate();
        if (!validation.IsSuccess)
            throw new InvalidOperationException(validation.Error!.MessageKey);
        return statement;
    }
}
