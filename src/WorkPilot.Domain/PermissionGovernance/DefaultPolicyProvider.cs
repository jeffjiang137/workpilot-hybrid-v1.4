using System;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;

namespace WorkPilot.Domain.PermissionGovernance;

/// <summary>
/// Produces the canonical minimum-permission default policy for each layer (PER-001 / T16 DoD:
/// "升级不会扩大 V1.4 权限"). The defaults are deliberately fail-closed:
/// <list type="bullet">
///   <item><description><see cref="PolicyLayer.BuiltInSafety"/> ships one hard rule: Deny every
///     Critical capability from any source/subject (doc 07 §5).</description></item>
///   <item><description><see cref="PolicyLayer.GlobalPolicy"/>, <see cref="PolicyLayer.SpacePolicy"/>,
///     <see cref="PolicyLayer.ExpertPolicy"/> and <see cref="PolicyLayer.AutomationPolicy"/> ship with
///     <b>zero</b> statements — nothing is allowed by default, so new sources/capabilities never
///     inherit an old Allow and the upgrade cannot grant broader access than V1.4.</description></item>
/// </list>
/// A default version is always marked <see cref="PolicyVersion.IsDefault"/> and is immutable; editing
/// a policy creates a new non-default version (PER-009). Wildcard <c>*</c> Allow is structurally
/// impossible here because <see cref="PolicyStatement.HasWildcardAllow"/> would reject it.
/// </summary>
public static class DefaultPolicyProvider
{
    /// <summary>Source/capability selector that matches everything. Used only for Deny rules.</summary>
    public const string MatchAllSelector = "{\"match\":\"all\"}";

    private static readonly IReadOnlyList<PolicySubject> AllSubjects = new[]
    {
        PolicySubject.InteractiveUser, PolicySubject.AutomationPrincipal, PolicySubject.SystemMaintenance
    };

    /// <summary>Default statements for a layer. BuiltInSafety gets the Critical-deny floor; all other layers are empty.</summary>
    public static IReadOnlyList<PolicyStatement> GetDefaultStatements(PolicyLayer layer, IIdGenerator ids, PolicyVersionId versionId)
    {
        if (layer == PolicyLayer.BuiltInSafety)
        {
            var criticalDeny = PolicyStatement.Create(
                ids,
                versionId,
                enabled: true,
                PolicyEffect.Deny,
                AllSubjects,
                MatchAllSelector,
                MatchAllSelector,
                RiskLevel.Critical,
                RiskLevel.Critical,
                scope: null,
                Array.Empty<PolicyCondition>(),
                priority: 0);
            return new[] { criticalDeny };
        }

        return Array.Empty<PolicyStatement>();
    }

    /// <summary>
    /// Builds the default (minimum-permission) document + version for a layer/scope. The version's
    /// canonical hash is computed from the default statements so it can be stored and later verified.
    /// </summary>
    public static (PolicyDocument Document, PolicyVersion Version, IReadOnlyList<PolicyStatement> Statements) BuildDefault(
        PolicyLayer layer, string? scopeId, IIdGenerator ids, IClock clock)
    {
        var now = clock.UtcNow;
        var docId = PolicyDocumentId.Create(ids);
        var versionId = PolicyVersionId.Create(ids);
        var statements = GetDefaultStatements(layer, ids, versionId);
        var canonical = PolicyCanonicalizer.CanonicalizeStatements(statements);
        var hash = PolicyCanonicalizer.HashStatements(statements);

        var document = new PolicyDocument(docId, layer, scopeId, versionId, now, now);
        var version = new PolicyVersion(versionId, docId, 1, hash, canonical, true, now);
        return (document, version, statements);
    }
}
