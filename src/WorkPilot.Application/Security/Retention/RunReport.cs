using System;
using System.Collections.Generic;

namespace WorkPilot.Application.Security.Retention;

/// <summary>
/// A privacy-safe, exportable snapshot of one run (LOG-005). It intentionally contains NO prompt,
/// parameters, or results — those live in the run snapshot which is never serialized here. Only
/// statuses, timings, error codes, and a redacted event index are included.
/// </summary>
public sealed record RunReport(
    int SchemaVersion,
    DateTimeOffset GeneratedAtUtc,
    RunReportRun Run,
    IReadOnlyList<RunReportStep> Steps,
    IReadOnlyList<RunReportEvent> Events,
    string? DecisionTraceSummary,
    IReadOnlyList<string> ErrorCodes,
    string Hash);

public sealed record RunReportRun(
    string Id,
    string AutomationRevisionId,
    string TriggerKind,
    string Status,
    DateTimeOffset ScheduledAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    int Priority,
    string? FinalErrorCode,
    int ActiveDurationMs,
    int ModelTurnCount,
    int CapabilityCallCount,
    int ResultBytes,
    int CoalescedCount,
    int RecoveryCount);

public sealed record RunReportStep(
    string NodeId,
    string NodeKind,
    string Status,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    int DurationMs,
    string? ErrorCode);

public sealed record RunReportEvent(
    int Sequence,
    DateTimeOffset OccurredAtUtc,
    string Kind,
    string Level,
    string Code,
    string MessageKey,
    string CorrelationId);
