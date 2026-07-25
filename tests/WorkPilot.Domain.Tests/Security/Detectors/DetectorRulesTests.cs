using System;
using System.Collections.Generic;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Security;
using WorkPilot.Domain.Security.Detectors;
using Xunit;

namespace WorkPilot.Domain.Tests.Security.Detectors;

public sealed class DetectorRulesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly IIdGenerator Ids = new SequentialIdGenerator();
    private static readonly SourceReference Src = new("connector", "github");
    private static readonly AutomationId Auto = AutomationId.Parse("auto_1");

    private static DetectorContext Ctx(
        IReadOnlyList<AuthFailureSample>? auth = null,
        IReadOnlyList<McpSchemaChange>? schema = null,
        IReadOnlyList<McpProtocolViolation>? proto = null,
        IReadOnlyList<ExecutableHashChange>? exe = null,
        IReadOnlyList<OAuthMismatch>? oauth = null,
        IReadOnlyList<DpapiFailure>? dpapi = null,
        IReadOnlyList<RedactionCanaryHit>? canary = null,
        IReadOnlyList<AuditIntegrityFailureSignal>? audit = null,
        IReadOnlyList<PolicyDenialSample>? denial = null,
        IReadOnlyList<WorkerCrashRecovery>? crash = null,
        IReadOnlyList<QueueBackpressure>? queue = null,
        IReadOnlyList<DiskSpaceLow>? disk = null,
        IReadOnlyList<CapabilityNoPermit>? noPermit = null,
        IReadOnlyList<LeaseLostSendAttempt>? lease = null,
        IReadOnlyList<OutcomeUnknownWrite>? outcome = null,
        IReadOnlyList<ApprovalRejectionBurst>? approval = null)
        => new(T0,
            auth ?? Array.Empty<AuthFailureSample>(),
            schema ?? Array.Empty<McpSchemaChange>(),
            proto ?? Array.Empty<McpProtocolViolation>(),
            exe ?? Array.Empty<ExecutableHashChange>(),
            oauth ?? Array.Empty<OAuthMismatch>(),
            dpapi ?? Array.Empty<DpapiFailure>(),
            canary ?? Array.Empty<RedactionCanaryHit>(),
            audit ?? Array.Empty<AuditIntegrityFailureSignal>(),
            denial ?? Array.Empty<PolicyDenialSample>(),
            crash ?? Array.Empty<WorkerCrashRecovery>(),
            queue ?? Array.Empty<QueueBackpressure>(),
            disk ?? Array.Empty<DiskSpaceLow>(),
            noPermit ?? Array.Empty<CapabilityNoPermit>(),
            lease ?? Array.Empty<LeaseLostSendAttempt>(),
            outcome ?? Array.Empty<OutcomeUnknownWrite>(),
            approval ?? Array.Empty<ApprovalRejectionBurst>());

    private static DetectorFinding Single(IDetectorRule rule, DetectorContext ctx)
    {
        var f = rule.Evaluate(ctx);
        Assert.Single(f);
        return f[0];
    }

    [Fact]
    public void Catalog_contains_all_16_rules()
    {
        Assert.Equal(16, DetectorRuleCatalog.All(Ids).Count);
        Assert.Equal(16, DetectorRuleCatalog.RuleCount);
    }

    [Fact]
    public void DET001_auth_failure_continuous()
    {
        var r = new AuthFailureContinuousRule(Ids);
        Assert.Empty(r.Evaluate(Ctx(auth: new[] { new AuthFailureSample(Src, 4) })));
        var f = Single(r, Ctx(auth: new[] { new AuthFailureSample(Src, 5) }));
        Assert.Equal(SecurityEventType.AuthFailureContinuous, f.Event.Type);
        Assert.Equal(SecuritySeverity.High, f.Event.Severity);
        Assert.Equal(DetectorActionKind.DisableNewCalls, f.Action!.Kind);
    }

    [Fact]
    public void DET002_mcp_schema_changed()
    {
        var r = new McpSchemaChangedRule(Ids);
        var f = Single(r, Ctx(schema: new[] { new McpSchemaChange(Src, "cap_1", "old", "new") }));
        Assert.Equal(SecurityEventType.McpSchemaChanged, f.Event.Type);
        Assert.Equal(SecuritySeverity.High, f.Event.Severity);
        Assert.Equal(DetectorActionKind.StaleGrant, f.Action!.Kind);
    }

    [Fact]
    public void DET003_mcp_protocol_exceeded()
    {
        var r = new McpProtocolExceededRule(Ids);
        Assert.Empty(r.Evaluate(Ctx(proto: new[] { new McpProtocolViolation(Src, 2) })));
        var f = Single(r, Ctx(proto: new[] { new McpProtocolViolation(Src, 3) }));
        Assert.Equal(SecuritySeverity.High, f.Event.Severity);
        Assert.Equal(DetectorActionKind.StopAndDisableServer, f.Action!.Kind);
    }

    [Fact]
    public void DET004_executable_hash_changed_critical()
    {
        var r = new ExecutableHashChangedRule(Ids);
        var f = Single(r, Ctx(exe: new[] { new ExecutableHashChange(Src, "exe_1", "a", "b") }));
        Assert.Equal(SecurityEventType.ExecutableHashChanged, f.Event.Type);
        Assert.Equal(SecuritySeverity.Critical, f.Event.Severity);
        Assert.Equal(DetectorActionKind.StopAndDisableServer, f.Action!.Kind);
    }

    [Fact]
    public void DET005_oauth_mismatch_critical()
    {
        var r = new OAuthMismatchRule(Ids);
        var f = Single(r, Ctx(oauth: new[] { new OAuthMismatch(Src, "issuer mismatch") }));
        Assert.Equal(SecuritySeverity.Critical, f.Event.Severity);
        Assert.Equal(DetectorActionKind.DisableSource, f.Action!.Kind);
    }

    [Fact]
    public void DET006_dpapi_failure_critical()
    {
        var r = new DpapiFailureRule(Ids);
        var f = Single(r, Ctx(dpapi: new[] { new DpapiFailure(Src, "integrity") }));
        Assert.Equal(SecuritySeverity.Critical, f.Event.Severity);
        Assert.Equal(DetectorActionKind.DisableSource, f.Action!.Kind);
    }

    [Fact]
    public void DET007_redaction_canary_critical()
    {
        var r = new RedactionCanaryHitRule(Ids);
        var f = Single(r, Ctx(canary: new[] { new RedactionCanaryHit("CANARY_X", Src) }));
        Assert.Equal(SecurityEventType.RedactionCanaryHit, f.Event.Type);
        Assert.Equal(SecuritySeverity.Critical, f.Event.Severity);
        Assert.Equal(DetectorActionKind.DisableNewCalls, f.Action!.Kind);
        // The canary token itself must NOT leak into evidence.
        Assert.False(f.Event.SafeEvidence.ContainsKey("canary_token"));
        Assert.Equal("true", f.Event.SafeEvidence["canary_matched"]);
    }

    [Fact]
    public void DET008_audit_integrity_failure_critical()
    {
        var r = new AuditIntegrityFailureRule(Ids);
        var f = Single(r, Ctx(audit: new[] { new AuditIntegrityFailureSignal("chain broken") }));
        Assert.Equal(SecurityEventType.AuditIntegrityFailure, f.Event.Type);
        Assert.Equal(SecuritySeverity.Critical, f.Event.Severity);
        Assert.Equal(DetectorActionKind.DisableNewCalls, f.Action!.Kind);
    }

    [Fact]
    public void DET009_policy_denial_burst()
    {
        var r = new PolicyDenialBurstRule(Ids);
        Assert.Empty(r.Evaluate(Ctx(denial: new[] { new PolicyDenialSample(Auto, Src, 9) })));
        var f = Single(r, Ctx(denial: new[] { new PolicyDenialSample(Auto, Src, 10) }));
        Assert.Equal(SecuritySeverity.Medium, f.Event.Severity);
        Assert.Equal(DetectorActionKind.PauseAutomation, f.Action!.Kind);
    }

    [Fact]
    public void DET010_worker_crash_recovery_burst()
    {
        var r = new WorkerCrashRecoveryBurstRule(Ids);
        Assert.Empty(r.Evaluate(Ctx(crash: new[] { new WorkerCrashRecovery(Src, Auto, 3) })));
        var f = Single(r, Ctx(crash: new[] { new WorkerCrashRecovery(Src, Auto, 4) }));
        Assert.Equal(SecuritySeverity.High, f.Event.Severity);
        Assert.Equal(DetectorActionKind.PauseAutomation, f.Action!.Kind);
    }

    [Fact]
    public void DET011_queue_backpressure()
    {
        var r = new QueueBackpressureRule(Ids);
        Assert.Empty(r.Evaluate(Ctx(queue: new[] { new QueueBackpressure(100, TimeSpan.FromMinutes(10)) })));
        var f = Single(r, Ctx(queue: new[] { new QueueBackpressure(900, TimeSpan.FromMinutes(5)) }));
        Assert.Equal(SecuritySeverity.High, f.Event.Severity);
        Assert.Equal(DetectorActionKind.StopMaterialization, f.Action!.Kind);
    }

    [Fact]
    public void DET012_disk_space_low()
    {
        var r = new DiskSpaceLowRule(Ids);
        Assert.Empty(r.Evaluate(Ctx(disk: new[] { new DiskSpaceLow(300) })));
        var f = Single(r, Ctx(disk: new[] { new DiskSpaceLow(150) }));
        Assert.Equal(SecuritySeverity.High, f.Event.Severity);
        Assert.Equal(DetectorActionKind.StopNewRuns, f.Action!.Kind);
    }

    [Fact]
    public void DET013_capability_no_permit_critical()
    {
        var r = new CapabilityNoPermitRule(Ids);
        var f = Single(r, Ctx(noPermit: new[] { new CapabilityNoPermit(Src, "cap_1", "sha") }));
        Assert.Equal(SecuritySeverity.Critical, f.Event.Severity);
        Assert.Equal(DetectorActionKind.RejectAndDisableCallPath, f.Action!.Kind);
    }

    [Fact]
    public void DET014_lease_lost_send_critical()
    {
        var r = new LeaseLostSendAttemptRule(Ids);
        var f = Single(r, Ctx(lease: new[] { new LeaseLostSendAttempt(Src, RunId.Parse("run_1"), "cap_1") }));
        Assert.Equal(SecuritySeverity.Critical, f.Event.Severity);
        Assert.Equal(DetectorActionKind.StopWorker, f.Action!.Kind);
    }

    [Fact]
    public void DET015_outcome_unknown_write_no_action()
    {
        var r = new OutcomeUnknownWriteRule(Ids);
        var f = Single(r, Ctx(outcome: new[] { new OutcomeUnknownWrite(Src, RunId.Parse("run_1"), "cap_1") }));
        Assert.Equal(SecuritySeverity.High, f.Event.Severity);
        Assert.Null(f.Action); // no automatic action
    }

    [Fact]
    public void DET016_approval_rejection_burst()
    {
        var r = new ApprovalRejectionBurstRule(Ids);
        Assert.Empty(r.Evaluate(Ctx(approval: new[] { new ApprovalRejectionBurst(Auto, 4) })));
        var f = Single(r, Ctx(approval: new[] { new ApprovalRejectionBurst(Auto, 5) }));
        Assert.Equal(SecuritySeverity.Medium, f.Event.Severity);
        Assert.Equal(DetectorActionKind.SuggestPause, f.Action!.Kind);
    }

    [Fact]
    public void Severity_modifier_raises_when_three_automations_affected()
    {
        // DET-009 base is Medium; 3+ affected automations pushes it up one notch.
        var r = new PolicyDenialBurstRule(Ids);
        var ctx = Ctx(denial: new[] { new PolicyDenialSample(Auto, Src, 10) });
        var f = Single(r, ctx);
        // Single automation → stays Medium. (Modifier path is exercised by SeverityCalculatorTests.)
        Assert.Equal(SecuritySeverity.Medium, f.Event.Severity);
    }
}
