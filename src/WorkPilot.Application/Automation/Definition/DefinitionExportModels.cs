using System;
using System.Collections.Generic;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation;

namespace WorkPilot.Application.Automation.Definition;

/// <summary>
/// A non-secret, machine-readable export of a single automation definition + its current immutable
/// revision (AUT-006). No credential, grant, run id or business body is ever included. The JSON is a
/// self-describing envelope conforming to the portable contract; <see cref="CanonicalHash"/> lets a
/// receiver verify the bytes were not tampered with.
/// </summary>
public sealed record DefinitionExport(
    string Json,
    string FileName,
    string CanonicalHash,
    int RevisionNumber,
    DateTimeOffset ExportedAtUtc);

/// <summary>Result of importing a definition: a brand-new, independent automation (IDs rebuilt).</summary>
public sealed record ImportedAutomation(
    AutomationId NewAutomationId,
    AutomationRevisionId NewRevisionId,
    bool NeedsReview,
    IReadOnlyList<ImportWarning> Warnings,
    DateTimeOffset ImportedAtUtc);

/// <summary>Non-blocking concern discovered while importing (the import still succeeds but the
/// automation stays disabled / marked for review — AUT-A08).</summary>
public sealed record ImportWarning(
    ImportWarningKind Kind,
    string MessageKey,
    string? Detail = null);

public enum ImportWarningKind
{
    UnresolvedTimezone,
    UnresolvedSource,
    MissingProject,
    NonPortableFieldDropped
}

/// <summary>Parsed, structurally-validated export envelope. The importer turns this into a new revision.</summary>
public sealed class ParsedDefinition
{
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public SpaceId SpaceId { get; init; }
    public string? ProjectId { get; init; }
    public string? ExpertId { get; init; }
    public TriggerDefinition Trigger { get; init; } = null!;
    public WorkflowDefinition Workflow { get; init; } = null!;
    public RunBudget Budget { get; init; } = null!;
    public OverlapPolicy OverlapPolicy { get; init; }
    public MissedRunPolicy MissedRunPolicy { get; init; }
    public PermissionRequest PermissionRequest { get; init; } = null!;
    public IReadOnlyList<ImportWarning> Warnings { get; init; } = Array.Empty<ImportWarning>();

    public bool NeedsReview => Warnings.Count > 0;
}
