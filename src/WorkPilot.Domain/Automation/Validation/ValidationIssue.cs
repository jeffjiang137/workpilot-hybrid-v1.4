namespace WorkPilot.Domain.Automation.Validation;

/// <summary>Severity of a validation finding. Errors block saving/enabling; Warnings are shown but allowed.</summary>
public enum ValidationSeverity
{
    Warning,
    Error
}

/// <summary>
/// A single structured validation finding. <see cref="Code"/> and <see cref="MessageKey"/> are
/// stable catalog constants (never inline literals). <see cref="JsonPointer"/> locates the finding
/// within the canonical automation document so the UI can scroll to it. <see cref="SafeDetails"/>
/// contains only non-sensitive, already-sanitized values.
/// </summary>
public sealed record ValidationIssue(
    ValidationSeverity Severity,
    string Code,
    string JsonPointer,
    string MessageKey,
    IReadOnlyDictionary<string, string>? SafeDetails = null)
{
    public string SafeDetailsText =>
        SafeDetails is null ? string.Empty
            : string.Join("; ", SafeDetails.Select(kv => $"{kv.Key}={kv.Value}"));
}

/// <summary>
/// Result of a validation pass. Issues are stored sorted by JSON Pointer ascending, then Code
/// ascending, so identical input always yields identical output (spec doc 03 §5: "相同输入顺序稳定").
/// </summary>
public sealed class ValidationResult
{
    public IReadOnlyList<ValidationIssue> Issues { get; private set; }

    public ValidationResult(IEnumerable<ValidationIssue> issues)
    {
        Issues = issues
            .OrderBy(i => i.JsonPointer, StringComparer.Ordinal)
            .ThenBy(i => i.Code, StringComparer.Ordinal)
            .ToArray();
    }

    public bool IsValid => Issues.All(i => i.Severity != ValidationSeverity.Error);
    public bool HasErrors => Issues.Any(i => i.Severity == ValidationSeverity.Error);
    public bool HasWarnings => Issues.Any(i => i.Severity == ValidationSeverity.Warning);

    public IReadOnlyList<ValidationIssue> Errors =>
        Issues.Where(i => i.Severity == ValidationSeverity.Error).ToArray();
    public IReadOnlyList<ValidationIssue> Warnings =>
        Issues.Where(i => i.Severity == ValidationSeverity.Warning).ToArray();

    public void AddRange(IEnumerable<ValidationIssue> more) =>
        Issues = Issues.Concat(more)
            .OrderBy(i => i.JsonPointer, StringComparer.Ordinal)
            .ThenBy(i => i.Code, StringComparer.Ordinal)
            .ToArray();

    public static ValidationResult Ok() => new(Array.Empty<ValidationIssue>());
}
