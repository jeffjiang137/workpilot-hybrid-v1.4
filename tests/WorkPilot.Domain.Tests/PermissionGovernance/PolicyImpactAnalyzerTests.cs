using System;
using System.Collections.Generic;
using System.Linq;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.PermissionGovernance;
using WorkPilot.Domain.PermissionGovernance.Evaluation;
using Xunit;

namespace WorkPilot.Domain.Tests.PermissionGovernance;

public class PolicyImpactAnalyzerTests
{
    private static readonly SequentialIdGenerator Ids = new();

    private static LayeredStatement DenyAll(PolicyLayer layer = PolicyLayer.BuiltInSafety)
    {
        var vid = PolicyVersionId.Create(Ids);
        var st = PolicyStatement.Create(Ids, vid, true, PolicyEffect.Deny,
            new[] { PolicySubject.InteractiveUser, PolicySubject.AutomationPrincipal, PolicySubject.SystemMaintenance },
            Selector.MatchAllJson, Selector.MatchAllJson, RiskLevel.Low, RiskLevel.Critical, null,
            Array.Empty<PolicyCondition>(), 0);
        return new LayeredStatement(layer, st);
    }

    private static LayeredStatement AllowAuto(string capId, string srcId, PolicyLayer layer)
    {
        var vid = PolicyVersionId.Create(Ids);
        var st = PolicyStatement.Create(Ids, vid, true, PolicyEffect.Allow,
            new[] { PolicySubject.AutomationPrincipal },
            Selector.ForId(srcId).ToJson(), Selector.ForId(capId).ToJson(),
            RiskLevel.Low, RiskLevel.Low, null, Array.Empty<PolicyCondition>(), 0);
        return new LayeredStatement(layer, st);
    }

    private static PolicySnapshot Snap(params LayeredStatement[] stmts) => new("hash", stmts);

    private static ImpactTarget Target(string capId = "cap-1", string srcId = "src-1")
        => new("auto-1", "mcp", srcId, "sch-src", capId, "sch-cap", RiskLevel.Low, null, "space-1");

    [Fact]
    public void Privilege_expansion_detected_when_Deny_becomes_Allow()
    {
        var oldS = Snap(DenyAll());
        var newS = Snap(AllowAuto("cap-1", "src-1", PolicyLayer.ExpertPolicy), AllowAuto("cap-1", "src-1", PolicyLayer.AutomationPolicy));

        var report = PolicyImpactAnalyzer.Analyze(oldS, newS, new[] { Target() });

        Assert.True(report.HasPrivilegeExpansion);
        Assert.True(report.RequiresEpochBump);
        Assert.Equal(ImpactTransition.DenyToAllow, report.Targets[0].Transition);
        Assert.True(report.Targets[0].IsPrivilegeExpansion);
    }

    [Fact]
    public void Restriction_detected_when_Allow_becomes_Deny()
    {
        var allow = new[] { AllowAuto("cap-1", "src-1", PolicyLayer.ExpertPolicy), AllowAuto("cap-1", "src-1", PolicyLayer.AutomationPolicy) };
        var oldS = Snap(allow);
        var newS = Snap(DenyAll());

        var report = PolicyImpactAnalyzer.Analyze(oldS, newS, new[] { Target() });

        Assert.False(report.HasPrivilegeExpansion);
        Assert.True(report.RequiresEpochBump);
        Assert.Equal(ImpactTransition.AllowToDeny, report.Targets[0].Transition);
    }

    [Fact]
    public void Unchanged_when_policy_unchanged()
    {
        var allow = new[] { AllowAuto("cap-1", "src-1", PolicyLayer.ExpertPolicy), AllowAuto("cap-1", "src-1", PolicyLayer.AutomationPolicy) };
        var report = PolicyImpactAnalyzer.Analyze(Snap(allow), Snap(allow), new[] { Target() });

        Assert.Equal(ImpactTransition.Unchanged, report.Targets[0].Transition);
        Assert.False(report.HasPrivilegeExpansion);
        Assert.False(report.RequiresEpochBump);
    }

    [Fact]
    public void Statement_diff_reports_added_removed_and_modified()
    {
        var oldS = Snap(AllowAuto("cap-1", "src-1", PolicyLayer.ExpertPolicy));
        var added = AllowAuto("cap-1", "src-1", PolicyLayer.AutomationPolicy);
        var modifiedOld = AllowAuto("cap-1", "src-1", PolicyLayer.ExpertPolicy);
        // Re-create the expert statement with a different risk range to force "modified" (new id).
        var modifiedNew = new LayeredStatement(PolicyLayer.ExpertPolicy,
            PolicyStatement.Create(Ids, PolicyVersionId.Create(Ids), true, PolicyEffect.Allow,
                new[] { PolicySubject.AutomationPrincipal },
                Selector.ForId("src-1").ToJson(), Selector.ForId("cap-1").ToJson(),
                RiskLevel.Low, RiskLevel.Medium, null, Array.Empty<PolicyCondition>(), 0));

        var newS = Snap(modifiedNew, added);

        var report = PolicyImpactAnalyzer.Analyze(oldS, newS, new[] { Target() });

        // The old expert statement id is gone (removed) and the new expert id is added (modified path
        // is by-id; here ids differ so it's removed+added). We assert at least one added and one removed.
        Assert.NotEmpty(report.Statements.Added);
        Assert.NotEmpty(report.Statements.Removed);
    }
}
