using System;
using System.Collections.Generic;
using System.Linq;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation.Run;

namespace WorkPilot.App.Core.Runs;

/// <summary>A projected, display-friendly row for the run history list (no step/event payload).</summary>
public sealed record RunListItemView(
    RunId Id,
    AutomationId? AutomationId,
    RunTriggerKind TriggerKind,
    RunStatus Status,
    int Priority,
    DateTimeOffset ScheduledAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    string? FinalErrorCode);

/// <summary>A single step execution row inside the run detail timeline.</summary>
public sealed record RunStepView(
    StepRunId Id,
    string NodeId,
    string NodeKind,
    StepRunStatus Status,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    int DurationMs,
    string? ErrorCode);

/// <summary>A status change derived from the run event sequence (LOG-002 detail timeline).</summary>
public sealed record StatusTransition(DateTimeOffset AtUtc, RunStatus To, string Code);

/// <summary>A fully projected run detail: header + step timeline + status transitions (LOG-002).</summary>
public sealed record RunDetailView(
    RunId Id,
    AutomationId? AutomationId,
    RunStatus Status,
    int Priority,
    DateTimeOffset ScheduledAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    RunId? ParentRunId,
    string? FinalErrorCode,
    IReadOnlyList<RunStepView> Steps,
    IReadOnlyList<StatusTransition> Transitions,
    int EventCount);

/// <summary>One safe field in an I/O summary (LOG-004): name + size only, never the value.</summary>
public sealed record SafeFieldSummary(string Name, int ByteSize, string? TargetAlias)
{
    /// <summary>True when the field is a target/recipient and is shown only as an alias/hash.</summary>
    public bool IsTarget => TargetAlias is not null;
}

/// <summary>A safe input/output summary: field names + sizes + target aliases, no body (LOG-004).</summary>
public sealed record SafeSummary(
    IReadOnlyList<SafeFieldSummary> Inputs,
    IReadOnlyList<SafeFieldSummary> Outputs,
    int InputBytes,
    int OutputBytes)
{
    public int InputCount => Inputs.Count;
    public int OutputCount => Outputs.Count;
    public bool HasTarget => Inputs.Any(i => i.IsTarget) || Outputs.Any(o => o.IsTarget);
}

/// <summary>A safe user notification envelope (RUN-008): a localized title key + a safe reason code, never body text or a secret.</summary>
public sealed record RunNotification(
    RunId RunId,
    RunStatus Status,
    string TitleMessageKey,
    string? ReasonCode,
    bool IsSecurityBlocked);

/// <summary>A pending High approval surfaced to the operator (RUN-007). Expires after the 10-minute decision window.</summary>
public sealed record ApprovalPrompt(
    RunId RunId,
    string ApprovalId,
    StepRunId StepId,
    string SafeSummaryJson,
    int RiskLevel,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc)
{
    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAtUtc;
    public TimeSpan Remaining(DateTimeOffset now) => ExpiresAtUtc - now;
}

/// <summary>A live change notification for a single run, delivered by <see cref="IRunFeed"/>.</summary>
public sealed record RunFeedItem(RunId RunId, IReadOnlyList<RunEvent> Events, bool Terminal);
