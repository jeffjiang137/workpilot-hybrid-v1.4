using System;
using System.Collections.Generic;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation.Run;

namespace WorkPilot.Application.Automation.Run;

/// <summary>Stable keyset cursor for run-list pagination (LOG-001). Null StartedAtUtc means "end of history".</summary>
public sealed record RunListCursor(DateTimeOffset? StartedAtUtc, RunId Id);

/// <summary>A projected run row for the history list (no step/event payload).</summary>
public sealed record RunListItem(
    RunId Id,
    AutomationId? AutomationId,
    AutomationRevisionId AutomationRevisionId,
    RunTriggerKind TriggerKind,
    RunStatus Status,
    int Priority,
    DateTimeOffset ScheduledAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    string? FinalErrorCode);

/// <summary>Filter + pagination parameters for <see cref="IRunRepository.ListRunsAsync"/>.</summary>
public sealed record RunQuery(
    AutomationId? AutomationId = null,
    RunStatus? Status = null,
    RunTriggerKind? TriggerKind = null,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    int PageSize = 50,
    RunListCursor? Cursor = null);

/// <summary>A page of run list items plus the cursor for the next page.</summary>
public sealed record RunListPage(
    IReadOnlyList<RunListItem> Items,
    bool HasMore,
    RunListCursor? NextCursor);

/// <summary>A fully hydrated run: header + frozen snapshot + steps + events (LOG-002).</summary>
public sealed record RunWithDetails(
    AutomationRun Run,
    RunSnapshot Snapshot,
    IReadOnlyList<StepRun> Steps,
    IReadOnlyList<RunEvent> Events);
