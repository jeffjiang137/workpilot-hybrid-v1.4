namespace WorkPilot.Contracts.Primitives;

/// <summary>
/// Versioned operational limits. All "magic numbers" MUST live here (AI dev rule §51); no
/// literal bounds anywhere else in the codebase. Adjust values only by publishing a new version.
/// Values are taken from the V1.5 spec (docs 03/04, schema automation-definition.schema.json).
/// </summary>
public static class Limits
{
    /// <summary>v1.5 limits. Bump this class (or fork a V1_6) when bounds change.</summary>
    public static class V1_5
    {
        // Identity
        public const int MaxEntityIdLength = 128;

        // Automation definition (authoritative per domain spec §1.1: name 1–80, description ≤500)
        public const int MaxAutomationNameLength = 80;
        public const int MaxAutomationDescriptionLength = 500;
        public const int MaxVariableNameLength = 32;   // id pattern ^[a-z][a-z0-9_]{0,31}$
        public const int MaxVariableCount = 256;
        public const int MaxNodeIdLength = 32;         // same id pattern as variables

        // Workflow (schema: nodes 1–32, edges 0–64)
        public const int MaxWorkflowNodes = 32;
        public const int MaxWorkflowEdges = 64;
        public const int MaxWorkflowNodeDisplayNameLength = 60;
        public const int MinWorkflowNodeTimeoutSeconds = 5;
        public const int MaxWorkflowNodeTimeoutSeconds = 1800;
        public const int MaxRetryMaxAttempts = 3;
        public const int MaxRetryBaseDelaySeconds = 60;
        public const int MaxRetryMaxDelaySeconds = 300;
        // Upper bound on a single retry wait; server Retry-After / jitter above this Defers (doc 04 §10).
        public const int MaxRetryWaitSeconds = 900; // 15 minutes
        public const int MaxAgentInstructionLength = 8000;
        public const int MaxAgentInputBindings = 20;
        public const int MaxAgentModelTurns = 8;
        public const int MaxConditionDepth = 5;
        public const int MaxConditionLeaves = 20;
        public const int MinDelaySeconds = 60;
        public const int MaxDelaySeconds = 86400;
        public const int MaxNotificationTitleLength = 80;
        public const int MaxNotificationBodyLength = 200;

        // Trigger / schedule (spec doc 03 §2, doc 04 §2.1/§2.2)
        public const int MinIntervalSeconds = 60;
        public const int MaxIntervalSeconds = 2_592_000; // 30 days
        public const int MaxCalendarHorizonYears = 5;    // candidate dates enumerated up to 5 years
        public const int MaxDaysOfWeek = 7;
        public const int MinDayOfMonth = 1;
        public const int MaxDayOfMonth = 31;
        public const int MaxDomainEventFilters = 20;
        public const int MaxFilterValueLength = 200;
        public const int MaxCatchUpRuns = 5;             // missed-run catch_up caps at 5 (RUN-010)
        public const int MaxTriggerIdLength = MaxEntityIdLength;

        // Run / execution
        // Default total-token budget per run. The portable export schema (automation-definition.schema.json)
        // does not carry max_total_tokens, so an imported definition derives it from this constant.
        public const int DefaultRunTotalTokenBudget = 64 * 1024;
        public const int MaxConcurrentRunsPerHost = 16;
        public const int MaxRunLogEvents = 100_000;
        public const int DefaultLeaseSeconds = 60;
        public const int MaxLeaseSeconds = 600;
        public const int MaxStepTimeoutSeconds = 3600;
        public const int MaxPayloadBytes = 4 * 1024 * 1024; // 4 MB

        // Retry / policy
        public const int MaxRetryAttempts = 8;
        public const int MaxPolicyRules = 1024;
        public const int MaxPolicyConditionsPerStatement = 10; // doc 07 §12: ≤10 conditions, AND-combined

        // AutomationGrant (PER-004): explicit, ≤30 days, revocable, scope-limited, Schema-bound.
        // Risk ceiling is fixed at Medium — a grant can never authorize High/Critical (doc 07 §8, §17).
        public const int MaxGrantDurationDays = 30;
        public const int MaxGrantsPerAutomationRevision = 16;

        // Impact analysis (PER-008): cap enumerated automations; results must be complete or save is blocked.
        public const int MaxImpactAnalysisTargets = 500;

        // Approval (PER-005): 10-minute decision window; 5-minute one-time receipt execution window.
        public const int ApprovalDecisionWindowMinutes = 10;
        public const int ConsentReceiptExecutionWindowMinutes = 5;
        public const int MaxApprovalRecoveryCount = 3; // >3 => RepeatedWorkerCrash (doc 04 §13)

        // Request bounds
        public const int MaxRequestTimeoutSeconds = 300;
        public const int MaxJsonDepth = 64;

        // Run Event / diagnostic redaction (doc 05 §3/§4/§5, LOG-002/003/004/007)
        public const int MaxSafePropertyValueLength = 2000; // string cap before truncated
        public const int MaxLogJsonDepth = 8;
        public const int MaxLogJsonArrayLength = 50;
        public const int MaxLogJsonObjectKeys = 50;
        public const int MaxRedactionStringLength = 2000;
        public const int DiagnosticSchemaVersion = 1;
        public const int DiagnosticChannelCapacity = 1000; // bounded channel; low levels dropped when full
        public const long DiagnosticMaxLogFileBytes = 10L * 1024 * 1024; // 10 MiB
        public const int DiagnosticMaxLogFiles = 5; // active + 4 rotated
        public const int MaxRunEventKinds = 64; // safety cap on catalog size

        // Retention / cleanup (doc 05 §9, SEC-106). Bounds mirror the 021 schema CHECK constraints.
        public const int RetentionDefaultRunDays = 90;
        public const int RetentionMinRunDays = 30;
        public const int RetentionMaxRunDays = 365;
        public const int RetentionDefaultEventDays = 30;
        public const int RetentionMinEventDays = 7;
        public const int RetentionMaxEventDays = 90;
        public const int RetentionDefaultAuditDays = 180;
        public const int RetentionMinAuditDays = 90;
        public const int RetentionMaxAuditDays = 730;
        public const int RetentionCleanupBatchSize = 500;        // rows per batch
        public const int RetentionCleanupMaxTransactionMs = 200; // soft budget per tx
        public const int RetentionCleanupMaxBatchesPerRun = 1000; // safety cap to bound a single run

        // Disk-space guard (SEC-107): below this, stop new automation + run cleanup + High incident.
        public const long RetentionDiskLowThresholdBytes = 200L * 1024 * 1024; // 200 MiB

        // Support package / export (doc 05 §10.2, LOG-006, SEC-108)
        public const long SupportBundleMaxBytes = 25L * 1024 * 1024; // 25 MiB zip cap
        public const int SupportBundleMaxRunReports = 20;
        public const int SupportBundleMaxDiagnosticFiles = 3;
        public const int SupportBundleMaxIncidents = 1000;
        public const int SupportBundleMaxAuditEntries = 10000;
        public const int RunReportSchemaVersion = 1;
    }
}
