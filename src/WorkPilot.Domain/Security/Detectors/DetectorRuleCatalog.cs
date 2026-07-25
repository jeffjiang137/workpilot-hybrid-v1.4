using System.Collections.Generic;
using System.Collections.Immutable;
using WorkPilot.Contracts.Primitives;

namespace WorkPilot.Domain.Security.Detectors;

/// <summary>The complete, ordered set of 16 fixed detector rules (doc 06 §4).</summary>
public static class DetectorRuleCatalog
{
    public static IReadOnlyList<IDetectorRule> All(IIdGenerator ids) => new IDetectorRule[]
    {
        new AuthFailureContinuousRule(ids),
        new McpSchemaChangedRule(ids),
        new McpProtocolExceededRule(ids),
        new ExecutableHashChangedRule(ids),
        new OAuthMismatchRule(ids),
        new DpapiFailureRule(ids),
        new RedactionCanaryHitRule(ids),
        new AuditIntegrityFailureRule(ids),
        new PolicyDenialBurstRule(ids),
        new WorkerCrashRecoveryBurstRule(ids),
        new QueueBackpressureRule(ids),
        new DiskSpaceLowRule(ids),
        new CapabilityNoPermitRule(ids),
        new LeaseLostSendAttemptRule(ids),
        new OutcomeUnknownWriteRule(ids),
        new ApprovalRejectionBurstRule(ids)
    }.ToImmutableArray();

    public static int RuleCount => 16;
}
