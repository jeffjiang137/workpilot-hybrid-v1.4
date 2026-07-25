using System.Collections.Generic;
using System.Collections.Immutable;

namespace WorkPilot.Contracts.Primitives;

/// <summary>
/// Canonical, serializable error. Matches the v1.5 error contract (spec §13):
/// a code + category + localized message key + retryability + safe details + correlation id.
/// Third-party text is mapped to category/code only and never enters MessageKey/SafeDetails.
/// </summary>
public sealed record AppError
{
    public string Code { get; }
    public ErrorCategory Category { get; }
    public string MessageKey { get; }
    public bool IsRetryable { get; }
    public IReadOnlyDictionary<string, string> SafeDetails { get; }
    public string CorrelationId { get; }

    public AppError(
        string code,
        ErrorCategory category,
        string messageKey,
        bool isRetryable,
        IReadOnlyDictionary<string, string>? safeDetails = null,
        string? correlationId = null)
    {
        Code = code;
        Category = category;
        MessageKey = messageKey;
        IsRetryable = isRetryable;
        SafeDetails = safeDetails ?? ImmutableDictionary<string, string>.Empty;
        CorrelationId = correlationId ?? string.Empty;
    }
}
