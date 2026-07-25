using System;
using System.Collections.Generic;
using System.Linq;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.PermissionGovernance;
using Xunit;

namespace WorkPilot.Domain.Tests.PermissionGovernance;

/// <summary>T16: default minimum-permission policy (PER-001), canonical hashing (PER-009), and statement validation.</summary>
public sealed class DefaultPolicyTests
{
    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Now => UtcNow;
    }

    private static readonly IIdGenerator Ids = new SequentialIdGenerator();
    private static readonly IClock Clock = new FixedClock();

    [Fact]
    public void BuiltInSafety_default_is_critical_deny_floor()
    {
        var (doc, version, statements) = DefaultPolicyProvider.BuildDefault(PolicyLayer.BuiltInSafety, null, Ids, Clock);
        Assert.Equal(PolicyLayer.BuiltInSafety, doc.Layer);
        Assert.Single(statements);
        var s = statements[0];
        Assert.Equal(PolicyEffect.Deny, s.Effect);
        Assert.Equal(RiskLevel.Critical, s.RiskMin);
        Assert.Equal(RiskLevel.Critical, s.RiskMax);
        Assert.Null(s.Scope);
        Assert.False(s.HasWildcardAllow());
        Assert.True(version.IsDefault);
    }

    [Theory]
    [InlineData(PolicyLayer.GlobalPolicy)]
    [InlineData(PolicyLayer.SpacePolicy)]
    [InlineData(PolicyLayer.ExpertPolicy)]
    [InlineData(PolicyLayer.AutomationPolicy)]
    public void NonBuiltIn_default_layers_are_empty_minimum_permission(PolicyLayer layer)
    {
        var (_, _, statements) = DefaultPolicyProvider.BuildDefault(layer, null, Ids, Clock);
        Assert.Empty(statements); // fail-closed: nothing allowed by default
    }

    [Fact]
    public void Default_baseline_never_uses_wildcard_allow()
    {
        foreach (var layer in new[] { PolicyLayer.BuiltInSafety, PolicyLayer.GlobalPolicy, PolicyLayer.SpacePolicy, PolicyLayer.ExpertPolicy, PolicyLayer.AutomationPolicy })
        {
            var (_, _, statements) = DefaultPolicyProvider.BuildDefault(layer, null, Ids, Clock);
            Assert.All(statements, s => Assert.False(s.HasWildcardAllow()));
            Assert.All(statements, s => Assert.NotEqual(PolicyEffect.Allow, s.Effect));
        }
    }

    [Fact]
    public void BuildDefault_canonical_hash_matches_recomputed()
    {
        var (_, version, statements) = DefaultPolicyProvider.BuildDefault(PolicyLayer.BuiltInSafety, null, Ids, Clock);
        var recomputed = PolicyCanonicalizer.HashStatements(statements);
        Assert.Equal(recomputed, version.CanonicalSha256);
        Assert.Equal(64, version.CanonicalSha256.Length);
    }

    [Fact]
    public void Canonicalizer_is_order_independent()
    {
        var vid = PolicyVersionId.Create(Ids);
        var a = new PolicyStatement(PolicyStatementId.Parse("s_a"), vid, true, PolicyEffect.Allow,
            new[] { PolicySubject.InteractiveUser }, "{\"source\":\"x\"}", "{\"capability\":\"c1\"}",
            RiskLevel.Low, RiskLevel.Low, null, Array.Empty<PolicyCondition>(), 0);
        var b = new PolicyStatement(PolicyStatementId.Parse("s_b"), vid, true, PolicyEffect.Deny,
            new[] { PolicySubject.AutomationPrincipal }, "{\"source\":\"y\"}", "{\"capability\":\"c2\"}",
            RiskLevel.High, RiskLevel.High, null, Array.Empty<PolicyCondition>(), 0);

        var hash1 = PolicyCanonicalizer.HashStatements(new[] { a, b });
        var hash2 = PolicyCanonicalizer.HashStatements(new[] { b, a });
        Assert.Equal(hash1, hash2); // sorted by id → deterministic
    }

    [Fact]
    public void Statement_validation_rejects_wildcard_allow()
    {
        var vid = PolicyVersionId.Create(Ids);
        // Constructed directly (bypassing Create's validation) so we can assert Validate() rejects it.
        var wildcard = new PolicyStatement(PolicyStatementId.Create(Ids), vid, true, PolicyEffect.Allow,
            new[] { PolicySubject.AutomationPrincipal }, "{\"source\":\"*\"}", "{\"capability\":\"*\"}",
            RiskLevel.Low, RiskLevel.Low, null, Array.Empty<PolicyCondition>(), 0);
        Assert.True(wildcard.HasWildcardAllow());
        Assert.False(wildcard.Validate().IsSuccess);
    }

    [Fact]
    public void Statement_validation_rejects_too_many_conditions()
    {
        var vid = PolicyVersionId.Create(Ids);
        var conditions = Enumerable.Range(0, Limits.V1_5.MaxPolicyConditionsPerStatement + 1)
            .Select(_ => new PolicyCondition(PolicyConditionKind.TimeWindow, "{}"))
            .ToList();
        var s = new PolicyStatement(PolicyStatementId.Create(Ids), vid, true, PolicyEffect.Allow,
            new[] { PolicySubject.InteractiveUser }, "{\"source\":\"x\"}", "{\"capability\":\"c\"}",
            RiskLevel.Low, RiskLevel.Low, null, conditions, 0);
        Assert.False(s.Validate().IsSuccess);
    }

    [Fact]
    public void Statement_validation_rejects_inverted_risk_range()
    {
        var vid = PolicyVersionId.Create(Ids);
        var s = new PolicyStatement(PolicyStatementId.Create(Ids), vid, true, PolicyEffect.Allow,
            new[] { PolicySubject.InteractiveUser }, "{\"source\":\"x\"}", "{\"capability\":\"c\"}",
            RiskLevel.High, RiskLevel.Low, null, Array.Empty<PolicyCondition>(), 0);
        Assert.False(s.Validate().IsSuccess);
    }

    [Fact]
    public void Statement_validation_rejects_unknown_condition()
    {
        var vid = PolicyVersionId.Create(Ids);
        var s = new PolicyStatement(PolicyStatementId.Create(Ids), vid, true, PolicyEffect.Allow,
            new[] { PolicySubject.InteractiveUser }, "{\"source\":\"x\"}", "{\"capability\":\"c\"}",
            RiskLevel.Low, RiskLevel.Low, null,
            new[] { new PolicyCondition(PolicyConditionKind.Unknown, "{}") }, 0);
        Assert.False(s.Validate().IsSuccess);
    }
}
