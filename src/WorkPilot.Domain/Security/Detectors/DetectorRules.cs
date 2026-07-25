using System.Collections.Generic;
using System.Globalization;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Security;

namespace WorkPilot.Domain.Security.Detectors;

/// <summary>The 16 fixed detector rules (doc 06 §4), implemented as pure evaluators.</summary>
public sealed class AuthFailureContinuousRule : IDetectorRule
{
    private readonly IIdGenerator _ids;
    public AuthFailureContinuousRule(IIdGenerator ids) => _ids = ids;
    public string Id => DetectorId.AuthFailureContinuous;
    public SecurityEventType EventType => SecurityEventType.AuthFailureContinuous;
    public IReadOnlyList<DetectorFinding> Evaluate(DetectorContext ctx)
    {
        var out_ = new List<DetectorFinding>();
        foreach (var s in ctx.AuthFailures)
        {
            if (s.Count < DetectorThresholds.AuthFailureCount) continue;
            var ev = new Dictionary<string, string> { ["source"] = s.Source.CompositeKey, ["count"] = s.Count.ToString(CultureInfo.InvariantCulture) };
            var e = DetectorEventBuilder.Build(_ids, EventType, SecuritySeverity.High, s.Source, null, null, "AUTH_FAILURES", ctx.ObservedAtUtc, ev);
            var a = new DetectorAction($"{Id}:source:{s.Source.CompositeKey}", DetectorActionKind.DisableNewCalls, "source", s.Source.CompositeKey);
            out_.Add(new DetectorFinding(e, a));
        }
        return out_;
    }
}

public sealed class McpSchemaChangedRule : IDetectorRule
{
    private readonly IIdGenerator _ids;
    public McpSchemaChangedRule(IIdGenerator ids) => _ids = ids;
    public string Id => DetectorId.McpSchemaChanged;
    public SecurityEventType EventType => SecurityEventType.McpSchemaChanged;
    public IReadOnlyList<DetectorFinding> Evaluate(DetectorContext ctx)
    {
        var out_ = new List<DetectorFinding>();
        foreach (var s in ctx.McpSchemaChanges)
        {
            var ev = new Dictionary<string, string>
            {
                ["source"] = s.Source.CompositeKey,
                ["capability_stable_id"] = s.CapabilityStableId,
                ["schema_old_sha"] = s.OldSchemaSha256.Length > 12 ? s.OldSchemaSha256.Substring(0, 12) : s.OldSchemaSha256,
                ["schema_new_sha"] = s.NewSchemaSha256.Length > 12 ? s.NewSchemaSha256.Substring(0, 12) : s.NewSchemaSha256
            };
            var e = DetectorEventBuilder.Build(_ids, EventType, SecuritySeverity.High, s.Source, null, s.CapabilityStableId, "MCP_SCHEMA_CHANGED", ctx.ObservedAtUtc, ev);
            var a = new DetectorAction($"{Id}:source:{s.Source.CompositeKey}", DetectorActionKind.StaleGrant, "source", s.Source.CompositeKey);
            out_.Add(new DetectorFinding(e, a));
        }
        return out_;
    }
}

public sealed class McpProtocolExceededRule : IDetectorRule
{
    private readonly IIdGenerator _ids;
    public McpProtocolExceededRule(IIdGenerator ids) => _ids = ids;
    public string Id => DetectorId.McpProtocolExceeded;
    public SecurityEventType EventType => SecurityEventType.McpProtocolExceeded;
    public IReadOnlyList<DetectorFinding> Evaluate(DetectorContext ctx)
    {
        var out_ = new List<DetectorFinding>();
        foreach (var s in ctx.McpProtocolViolations)
        {
            if (s.Count < DetectorThresholds.McpProtocolViolationCount) continue;
            var ev = new Dictionary<string, string> { ["source"] = s.Source.CompositeKey, ["count"] = s.Count.ToString(CultureInfo.InvariantCulture) };
            var e = DetectorEventBuilder.Build(_ids, EventType, SecuritySeverity.High, s.Source, null, null, "MCP_PROTOCOL", ctx.ObservedAtUtc, ev);
            var a = new DetectorAction($"{Id}:source:{s.Source.CompositeKey}", DetectorActionKind.StopAndDisableServer, "source", s.Source.CompositeKey);
            out_.Add(new DetectorFinding(e, a));
        }
        return out_;
    }
}

public sealed class ExecutableHashChangedRule : IDetectorRule
{
    private readonly IIdGenerator _ids;
    public ExecutableHashChangedRule(IIdGenerator ids) => _ids = ids;
    public string Id => DetectorId.ExecutableHashChanged;
    public SecurityEventType EventType => SecurityEventType.ExecutableHashChanged;
    public IReadOnlyList<DetectorFinding> Evaluate(DetectorContext ctx)
    {
        var out_ = new List<DetectorFinding>();
        foreach (var s in ctx.ExecutableHashChanges)
        {
            var ev = new Dictionary<string, string>
            {
                ["source"] = s.Source.CompositeKey,
                ["executable_id"] = s.ExecutableId,
                ["hash_old"] = s.OldHash.Length > 12 ? s.OldHash.Substring(0, 12) : s.OldHash,
                ["hash_new"] = s.NewHash.Length > 12 ? s.NewHash.Substring(0, 12) : s.NewHash
            };
            var e = DetectorEventBuilder.Build(_ids, EventType, SecuritySeverity.Critical, s.Source, null, null, "EXECUTABLE_HASH", ctx.ObservedAtUtc, ev,
                involvesExecutable: true);
            var a = new DetectorAction($"{Id}:source:{s.Source.CompositeKey}", DetectorActionKind.StopAndDisableServer, "source", s.Source.CompositeKey);
            out_.Add(new DetectorFinding(e, a));
        }
        return out_;
    }
}

public sealed class OAuthMismatchRule : IDetectorRule
{
    private readonly IIdGenerator _ids;
    public OAuthMismatchRule(IIdGenerator ids) => _ids = ids;
    public string Id => DetectorId.OAuthMismatch;
    public SecurityEventType EventType => SecurityEventType.OAuthMismatch;
    public IReadOnlyList<DetectorFinding> Evaluate(DetectorContext ctx)
    {
        var out_ = new List<DetectorFinding>();
        foreach (var s in ctx.OAuthMismatches)
        {
            var ev = new Dictionary<string, string> { ["source"] = s.Source.CompositeKey, ["detail"] = s.Detail.Length > 120 ? s.Detail.Substring(0, 120) : s.Detail };
            var e = DetectorEventBuilder.Build(_ids, EventType, SecuritySeverity.Critical, s.Source, null, null, "OAUTH_MISMATCH", ctx.ObservedAtUtc, ev,
                involvesCredential: true);
            var a = new DetectorAction($"{Id}:source:{s.Source.CompositeKey}", DetectorActionKind.DisableSource, "source", s.Source.CompositeKey);
            out_.Add(new DetectorFinding(e, a));
        }
        return out_;
    }
}

public sealed class DpapiFailureRule : IDetectorRule
{
    private readonly IIdGenerator _ids;
    public DpapiFailureRule(IIdGenerator ids) => _ids = ids;
    public string Id => DetectorId.DpapiFailure;
    public SecurityEventType EventType => SecurityEventType.DpapiFailure;
    public IReadOnlyList<DetectorFinding> Evaluate(DetectorContext ctx)
    {
        var out_ = new List<DetectorFinding>();
        foreach (var s in ctx.DpapiFailures)
        {
            var ev = new Dictionary<string, string> { ["source"] = s.Source.CompositeKey, ["detail"] = s.Detail.Length > 120 ? s.Detail.Substring(0, 120) : s.Detail };
            var e = DetectorEventBuilder.Build(_ids, EventType, SecuritySeverity.Critical, s.Source, null, null, "DPAPI_FAILURE", ctx.ObservedAtUtc, ev,
                involvesCredential: true);
            var a = new DetectorAction($"{Id}:source:{s.Source.CompositeKey}", DetectorActionKind.DisableSource, "source", s.Source.CompositeKey);
            out_.Add(new DetectorFinding(e, a));
        }
        return out_;
    }
}

public sealed class RedactionCanaryHitRule : IDetectorRule
{
    private readonly IIdGenerator _ids;
    public RedactionCanaryHitRule(IIdGenerator ids) => _ids = ids;
    public string Id => DetectorId.RedactionCanaryHit;
    public SecurityEventType EventType => SecurityEventType.RedactionCanaryHit;
    public IReadOnlyList<DetectorFinding> Evaluate(DetectorContext ctx)
    {
        var out_ = new List<DetectorFinding>();
        foreach (var s in ctx.RedactionCanaryHits)
        {
            var target = s.Source?.CompositeKey ?? "host:local";
            var ev = new Dictionary<string, string> { ["canary_matched"] = "true", ["source"] = target };
            var e = DetectorEventBuilder.Build(_ids, EventType, SecuritySeverity.Critical, s.Source, null, null, "REDACTION_CANARY", ctx.ObservedAtUtc, ev,
                involvesRedaction: true);
            var a = new DetectorAction($"{Id}:source:{target}", DetectorActionKind.DisableNewCalls, "source", target);
            out_.Add(new DetectorFinding(e, a));
        }
        return out_;
    }
}

public sealed class AuditIntegrityFailureRule : IDetectorRule
{
    private readonly IIdGenerator _ids;
    public AuditIntegrityFailureRule(IIdGenerator ids) => _ids = ids;
    public string Id => DetectorId.AuditIntegrityFailure;
    public SecurityEventType EventType => SecurityEventType.AuditIntegrityFailure;
    public IReadOnlyList<DetectorFinding> Evaluate(DetectorContext ctx)
    {
        var out_ = new List<DetectorFinding>();
        foreach (var s in ctx.AuditIntegrityFailures)
        {
            var ev = new Dictionary<string, string> { ["detail"] = s.Detail.Length > 200 ? s.Detail.Substring(0, 200) : s.Detail };
            var e = DetectorEventBuilder.Build(_ids, EventType, SecuritySeverity.Critical, null, null, null, "AUDIT_INTEGRITY", ctx.ObservedAtUtc, ev,
                involvesAudit: true);
            var a = new DetectorAction($"{Id}:host:local", DetectorActionKind.DisableNewCalls, "host", "local");
            out_.Add(new DetectorFinding(e, a));
        }
        return out_;
    }
}

public sealed class PolicyDenialBurstRule : IDetectorRule
{
    private readonly IIdGenerator _ids;
    public PolicyDenialBurstRule(IIdGenerator ids) => _ids = ids;
    public string Id => DetectorId.PolicyDenialBurst;
    public SecurityEventType EventType => SecurityEventType.PolicyDenialBurst;
    public IReadOnlyList<DetectorFinding> Evaluate(DetectorContext ctx)
    {
        var out_ = new List<DetectorFinding>();
        foreach (var s in ctx.PolicyDenials)
        {
            if (s.Count < DetectorThresholds.PolicyDenialCount) continue;
            var ev = new Dictionary<string, string>
            {
                ["automation_id"] = s.AutomationId.Value,
                ["count"] = s.Count.ToString(CultureInfo.InvariantCulture),
                ["source"] = s.Source?.CompositeKey ?? string.Empty
            };
            var e = DetectorEventBuilder.Build(_ids, EventType, SecuritySeverity.Medium, s.Source, s.AutomationId, null, "POLICY_DENIAL_BURST", ctx.ObservedAtUtc, ev);
            var a = new DetectorAction($"{Id}:automation:{s.AutomationId.Value}", DetectorActionKind.PauseAutomation, "automation", s.AutomationId.Value);
            out_.Add(new DetectorFinding(e, a));
        }
        return out_;
    }
}

public sealed class WorkerCrashRecoveryBurstRule : IDetectorRule
{
    private readonly IIdGenerator _ids;
    public WorkerCrashRecoveryBurstRule(IIdGenerator ids) => _ids = ids;
    public string Id => DetectorId.WorkerCrashRecoveryBurst;
    public SecurityEventType EventType => SecurityEventType.WorkerCrashRecoveryBurst;
    public IReadOnlyList<DetectorFinding> Evaluate(DetectorContext ctx)
    {
        var out_ = new List<DetectorFinding>();
        foreach (var s in ctx.WorkerCrashRecoveries)
        {
            if (s.Count <= DetectorThresholds.WorkerCrashRecoveryCount) continue; // strictly greater than 3
            var target = s.AutomationId?.Value ?? "host:local";
            var ev = new Dictionary<string, string>
            {
                ["automation_id"] = s.AutomationId?.Value ?? string.Empty,
                ["count"] = s.Count.ToString(CultureInfo.InvariantCulture),
                ["source"] = s.Source?.CompositeKey ?? string.Empty
            };
            var e = DetectorEventBuilder.Build(_ids, EventType, SecuritySeverity.High, s.Source, s.AutomationId, null, "WORKER_CRASH", ctx.ObservedAtUtc, ev);
            var a = new DetectorAction($"{Id}:automation:{target}", DetectorActionKind.PauseAutomation, "automation", target);
            out_.Add(new DetectorFinding(e, a));
        }
        return out_;
    }
}

public sealed class QueueBackpressureRule : IDetectorRule
{
    private readonly IIdGenerator _ids;
    public QueueBackpressureRule(IIdGenerator ids) => _ids = ids;
    public string Id => DetectorId.QueueBackpressure;
    public SecurityEventType EventType => SecurityEventType.QueueBackpressure;
    public IReadOnlyList<DetectorFinding> Evaluate(DetectorContext ctx)
    {
        var out_ = new List<DetectorFinding>();
        foreach (var s in ctx.QueueBackpressures)
        {
            if (s.Depth <= DetectorThresholds.QueueDepthLimit && s.OldestWait <= DetectorThresholds.QueueOldestWaitLimit) continue;
            var ev = new Dictionary<string, string>
            {
                ["depth"] = s.Depth.ToString(CultureInfo.InvariantCulture),
                ["oldest_wait_min"] = s.OldestWait.TotalMinutes.ToString("F1", CultureInfo.InvariantCulture)
            };
            var e = DetectorEventBuilder.Build(_ids, EventType, SecuritySeverity.High, null, null, null, "QUEUE_BACKPRESSURE", ctx.ObservedAtUtc, ev);
            var a = new DetectorAction($"{Id}:host:local", DetectorActionKind.StopMaterialization, "host", "local");
            out_.Add(new DetectorFinding(e, a));
        }
        return out_;
    }
}

public sealed class DiskSpaceLowRule : IDetectorRule
{
    private readonly IIdGenerator _ids;
    public DiskSpaceLowRule(IIdGenerator ids) => _ids = ids;
    public string Id => DetectorId.DiskSpaceLow;
    public SecurityEventType EventType => SecurityEventType.DiskSpaceLow;
    public IReadOnlyList<DetectorFinding> Evaluate(DetectorContext ctx)
    {
        var out_ = new List<DetectorFinding>();
        foreach (var s in ctx.DiskSpaceLows)
        {
            if (s.FreeMiB >= DetectorThresholds.DiskFreeMiBLimit) continue;
            var ev = new Dictionary<string, string> { ["free_mib"] = s.FreeMiB.ToString(CultureInfo.InvariantCulture) };
            var e = DetectorEventBuilder.Build(_ids, EventType, SecuritySeverity.High, null, null, null, "DISK_LOW", ctx.ObservedAtUtc, ev);
            var a = new DetectorAction($"{Id}:host:local", DetectorActionKind.StopNewRuns, "host", "local");
            out_.Add(new DetectorFinding(e, a));
        }
        return out_;
    }
}

public sealed class CapabilityNoPermitRule : IDetectorRule
{
    private readonly IIdGenerator _ids;
    public CapabilityNoPermitRule(IIdGenerator ids) => _ids = ids;
    public string Id => DetectorId.CapabilityNoPermit;
    public SecurityEventType EventType => SecurityEventType.CapabilityNoPermit;
    public IReadOnlyList<DetectorFinding> Evaluate(DetectorContext ctx)
    {
        var out_ = new List<DetectorFinding>();
        foreach (var s in ctx.CapabilityNoPermits)
        {
            var ev = new Dictionary<string, string>
            {
                ["source"] = s.Source.CompositeKey,
                ["capability_stable_id"] = s.CapabilityStableId,
                ["schema_sha"] = s.SchemaSha256.Length > 12 ? s.SchemaSha256.Substring(0, 12) : s.SchemaSha256
            };
            var e = DetectorEventBuilder.Build(_ids, EventType, SecuritySeverity.Critical, s.Source, null, s.CapabilityStableId, "NO_PERMIT", ctx.ObservedAtUtc, ev);
            var a = new DetectorAction($"{Id}:source:{s.Source.CompositeKey}", DetectorActionKind.RejectAndDisableCallPath, "source", s.Source.CompositeKey);
            out_.Add(new DetectorFinding(e, a));
        }
        return out_;
    }
}

public sealed class LeaseLostSendAttemptRule : IDetectorRule
{
    private readonly IIdGenerator _ids;
    public LeaseLostSendAttemptRule(IIdGenerator ids) => _ids = ids;
    public string Id => DetectorId.LeaseLostSendAttempt;
    public SecurityEventType EventType => SecurityEventType.LeaseLostSendAttempt;
    public IReadOnlyList<DetectorFinding> Evaluate(DetectorContext ctx)
    {
        var out_ = new List<DetectorFinding>();
        foreach (var s in ctx.LeaseLostSendAttempts)
        {
            var ev = new Dictionary<string, string>
            {
                ["source"] = s.Source.CompositeKey,
                ["run_id"] = s.RunId.Value,
                ["capability_stable_id"] = s.CapabilityStableId
            };
            var e = DetectorEventBuilder.Build(_ids, EventType, SecuritySeverity.Critical, s.Source, null, s.CapabilityStableId, "LEASE_LOST_SEND", ctx.ObservedAtUtc, ev);
            var a = new DetectorAction($"{Id}:source:{s.Source.CompositeKey}", DetectorActionKind.StopWorker, "source", s.Source.CompositeKey);
            out_.Add(new DetectorFinding(e, a));
        }
        return out_;
    }
}

public sealed class OutcomeUnknownWriteRule : IDetectorRule
{
    private readonly IIdGenerator _ids;
    public OutcomeUnknownWriteRule(IIdGenerator ids) => _ids = ids;
    public string Id => DetectorId.OutcomeUnknownWrite;
    public SecurityEventType EventType => SecurityEventType.OutcomeUnknownWrite;
    public IReadOnlyList<DetectorFinding> Evaluate(DetectorContext ctx)
    {
        var out_ = new List<DetectorFinding>();
        foreach (var s in ctx.OutcomeUnknownWrites)
        {
            // No automatic action (doc 06 §4: "无自动重试"); the run is flagged needs_review by the caller.
            var ev = new Dictionary<string, string>
            {
                ["source"] = s.Source.CompositeKey,
                ["run_id"] = s.RunId.Value,
                ["capability_stable_id"] = s.CapabilityStableId
            };
            var e = DetectorEventBuilder.Build(_ids, EventType, SecuritySeverity.High, s.Source, null, s.CapabilityStableId, "OUTCOME_UNKNOWN", ctx.ObservedAtUtc, ev,
                externalSideEffectUnknown: true);
            out_.Add(new DetectorFinding(e, null));
        }
        return out_;
    }
}

public sealed class ApprovalRejectionBurstRule : IDetectorRule
{
    private readonly IIdGenerator _ids;
    public ApprovalRejectionBurstRule(IIdGenerator ids) => _ids = ids;
    public string Id => DetectorId.ApprovalRejectionBurst;
    public SecurityEventType EventType => SecurityEventType.ApprovalRejectionBurst;
    public IReadOnlyList<DetectorFinding> Evaluate(DetectorContext ctx)
    {
        var out_ = new List<DetectorFinding>();
        foreach (var s in ctx.ApprovalRejectionBursts)
        {
            if (s.Count < DetectorThresholds.ApprovalRejectionCount) continue;
            var ev = new Dictionary<string, string>
            {
                ["automation_id"] = s.AutomationId.Value,
                ["count"] = s.Count.ToString(CultureInfo.InvariantCulture)
            };
            var e = DetectorEventBuilder.Build(_ids, EventType, SecuritySeverity.Medium, null, s.AutomationId, null, "APPROVAL_REJECTED", ctx.ObservedAtUtc, ev);
            var a = new DetectorAction($"{Id}:automation:{s.AutomationId.Value}", DetectorActionKind.SuggestPause, "automation", s.AutomationId.Value);
            out_.Add(new DetectorFinding(e, a));
        }
        return out_;
    }
}
