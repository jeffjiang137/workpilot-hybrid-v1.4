using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Application.Permission.Policy;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.PermissionGovernance;
using WorkPilot.Domain.PermissionGovernance.Evaluation;
using Xunit;

namespace WorkPilot.Application.Tests.Permission.Policy;

public class PolicyProjectionServiceTests
{
    private static readonly IIdGenerator Ids = new SequentialIdGenerator();
    private static readonly IClock Clock = new FakeClock(DateTimeOffset.UnixEpoch);

    private static PolicyStatement DenyAllFloor() =>
        PolicyStatement.Create(Ids, PolicyVersionId.Parse("v1"), true, PolicyEffect.Deny,
            new[] { PolicySubject.InteractiveUser, PolicySubject.AutomationPrincipal, PolicySubject.SystemMaintenance },
            Selector.MatchAllJson, Selector.MatchAllJson, RiskLevel.Low, RiskLevel.Critical, null,
            Array.Empty<PolicyCondition>(), 0);

    private static EvaluationContext Context() => new(
        PolicySubject.AutomationPrincipal, "src-1", "sch-src", true, false, true, "space-1",
        true, false, 1, true, DateTimeOffset.UnixEpoch, "interactive", "manual", 1, 0, "healthy");

    [Fact]
    public async Task Projection_returns_one_view_per_query_with_shared_evaluator_decision()
    {
        var store = new DenyFloorStore();
        var recorded = new List<PolicySimulationExecuted>();
        var simulator = new PolicySimulatorService(store, recorded.Add);
        var svc = new PolicyProjectionService(simulator);

        var queries = new[]
        {
            new CapabilityQuery("cap-1", "sch-src", RiskLevel.Low, null),
            new CapabilityQuery("cap-2", "sch-src", RiskLevel.Medium, null),
        };

        var views = await svc.ProjectAsync(Context(), queries, CancellationToken.None);

        // One view per query, and the built-in Deny floor (shared evaluator) denies both.
        Assert.Equal(2, views.Count);
        Assert.All(views, v =>
        {
            Assert.Equal(PermissionDecisionKind.Deny, v.Decision);
            Assert.Equal("ExplicitDeny", v.PrimaryReasonCode);
        });
        // The simulator (not the projection) records local metadata only — one per simulated capability.
        Assert.Equal(2, recorded.Count);
    }

    [Fact]
    public async Task Projection_fidelity_matches_direct_evaluator()
    {
        var store = new DenyFloorStore();
        var simulator = new PolicySimulatorService(store);
        var svc = new PolicyProjectionService(simulator);

        var ctx = Context();
        var query = new CapabilityQuery("cap-1", "sch-src", RiskLevel.Low, null);

        // Reference decision computed directly with the same pure evaluator + a hand-built snapshot
        // mirroring the store's BuiltInSafety Deny floor. Must agree (no simplified projection algo).
        var refSnapshot = new PolicySnapshot("h", new[] { new LayeredStatement(PolicyLayer.BuiltInSafety, DenyAllFloor()) });
        var refCap = new CapabilityDescriptor(query.StableId, query.SourceSchemaSha256, query.ArgumentRisk, query.InvocationScope);
        var refArgs = new EvaluationArguments(query.InvocationScope, query.ArgumentRisk);
        var refDecision = PolicyEvaluator.Evaluate(refSnapshot, ctx, refCap, refArgs, null);

        var views = await svc.ProjectAsync(ctx, new[] { query }, CancellationToken.None);

        Assert.Equal(refDecision.Kind, views[0].Decision);
        Assert.Equal(refDecision.PrimaryReasonCode, views[0].PrimaryReasonCode);
        Assert.Equal(refDecision.EffectiveRisk, views[0].EffectiveRisk);
    }

    /// <summary>Store that surfaces a BuiltInSafety Deny floor for every layer query.</summary>
    private sealed class DenyFloorStore : IPolicyStore
    {
        public Task<Result<PolicyVersionId>> SaveNewVersionAsync(
            PolicyDocumentId documentId, IReadOnlyList<PolicyStatement> statements,
            string actor, string reasonCode, IClock clock, CancellationToken ct = default)
            => Task.FromResult(Result<PolicyVersionId>.Ok(PolicyVersionId.Parse("ver-new")));

        public Task<Result<PolicyVersionId>> RecoverDefaultAsync(
            PolicyLayer layer, string? scopeId, IClock clock, CancellationToken ct = default)
            => Task.FromResult(Result<PolicyVersionId>.Ok(PolicyVersionId.Parse("ver-rec")));

        public Task<Result<CurrentPolicyBundle>> GetCurrentAsync(
            PolicyLayer layer, string? scopeId, CancellationToken ct = default)
        {
            var doc = new PolicyDocument(PolicyDocumentId.Parse("doc-1"), layer, scopeId,
                PolicyVersionId.Parse("ver-1"), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
            var ver = new PolicyVersion(PolicyVersionId.Parse("ver-1"), doc.Id, 1, "hash", "{}", false, DateTimeOffset.UnixEpoch);
            var statements = layer == PolicyLayer.BuiltInSafety
                ? new[] { DenyAllFloor() } : Array.Empty<PolicyStatement>();
            return Task.FromResult(Result<CurrentPolicyBundle>.Ok(new CurrentPolicyBundle(doc, ver, statements)));
        }

        public Task<Result<PolicyAuditPage>> ListAuditAsync(int limit, int offset, CancellationToken ct = default)
            => Task.FromResult(Result<PolicyAuditPage>.Ok(new PolicyAuditPage(Array.Empty<PolicyAuditRecord>(), 0)));

        public Task<Result<bool>> VerifyIntegrityAsync(CancellationToken ct = default)
            => Task.FromResult(Result<bool>.Ok(true));

        public Task InvalidateReceiptsForPolicyHashAsync(string policyHash, IClock clock, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task EnsureDefaultPolicyAsync(IClock clock, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
