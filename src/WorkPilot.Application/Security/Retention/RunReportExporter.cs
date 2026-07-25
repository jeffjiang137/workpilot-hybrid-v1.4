using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Application.Automation.Run;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation.Run;

namespace WorkPilot.Application.Security.Retention;

/// <summary>
/// Builds a <see cref="RunReport"/> from a stored run (LOG-005). The report excludes prompt/parameters/
/// results and redacted step I/O; a SHA-256 <see cref="RunReport.Hash"/> over the canonical JSON lets a
/// support engineer verify the export was not tampered with.
/// </summary>
public sealed class RunReportExporter : IRunReportExporter
{
    private readonly IRunRepository _runs;
    private readonly IClock _clock;

    public RunReportExporter(IRunRepository runs, IClock clock)
    {
        _runs = runs;
        _clock = clock;
    }

    public async Task<Result<RunReport>> BuildAsync(RunId id, CancellationToken ct = default)
    {
        var got = await _runs.GetRunAsync(id, ct).ConfigureAwait(false);
        if (!got.IsSuccess) return Result<RunReport>.Fail(got.Error!);
        var details = got.Value;
        if (details is null) return Result<RunReport>.Fail(RetentionAndExportErrors.RunReportNotFoundError(id.ToString()));

        var run = details.Run;
        var reportRun = new RunReportRun(
            Id: run.Id.ToString(),
            AutomationRevisionId: run.AutomationRevisionId.ToString(),
            TriggerKind: run.TriggerKind.ToString(),
            Status: run.Status.ToString(),
            ScheduledAtUtc: run.ScheduledAtUtc,
            StartedAtUtc: run.StartedAtUtc,
            FinishedAtUtc: run.FinishedAtUtc,
            Priority: run.Priority,
            FinalErrorCode: run.FinalErrorCode,
            ActiveDurationMs: run.ActiveDurationMs,
            ModelTurnCount: run.ModelTurnCount,
            CapabilityCallCount: run.CapabilityCallCount,
            ResultBytes: run.ResultBytes,
            CoalescedCount: run.CoalescedCount,
            RecoveryCount: run.RecoveryCount);

        var steps = details.Steps.Select(s => new RunReportStep(
            NodeId: s.NodeId,
            NodeKind: s.NodeKind,
            Status: s.Status.ToString(),
            StartedAtUtc: s.StartedAtUtc,
            FinishedAtUtc: s.FinishedAtUtc,
            DurationMs: s.DurationMs,
            ErrorCode: s.ErrorCode)).ToArray();

        // Events intentionally exclude SafePropertiesJson (already redacted, but kept out of exports).
        var events = details.Events.Select(e => new RunReportEvent(
            Sequence: e.Sequence,
            OccurredAtUtc: e.OccurredAtUtc,
            Kind: e.Kind,
            Level: e.Level.ToString(),
            Code: e.Code,
            MessageKey: e.MessageKey,
            CorrelationId: e.CorrelationId)).ToArray();

        var decisionSummary = DeriveDecisionTraceSummary(run);
        var errorCodes = steps.Select(x => x.ErrorCode).Where(x => !string.IsNullOrEmpty(x))
            .Concat(new[] { run.FinalErrorCode }.Where(x => !string.IsNullOrEmpty(x)))
            .Distinct().Select(x => x!).ToArray();

        var report = new RunReport(
            SchemaVersion: Limits.V1_5.RunReportSchemaVersion,
            GeneratedAtUtc: _clock.UtcNow,
            Run: reportRun,
            Steps: steps,
            Events: events,
            DecisionTraceSummary: decisionSummary,
            ErrorCodes: errorCodes,
            Hash: string.Empty);

        var hash = ComputeHash(report);
        return Result<RunReport>.Ok(report with { Hash = hash });
    }

    private static string? DeriveDecisionTraceSummary(AutomationRun run)
    {
        // A run that needed human gates surfaces that in its report; no embedded receipt is needed.
        if (run.Status == RunStatus.WaitingApproval || run.Status == RunStatus.BlockedPolicy)
            return "approval-required";
        return null;
    }

    private static string ComputeHash(RunReport report)
    {
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = false });
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }
}
