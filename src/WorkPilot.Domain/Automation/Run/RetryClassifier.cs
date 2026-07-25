using WorkPilot.Contracts.Primitives;

namespace WorkPilot.Domain.Automation.Run;

/// <summary>How an error should influence retry (doc 04 §10).</summary>
public enum RetryDisposition
{
    /// <summary>Transient, safe to retry (DNS/connect/408/429/5xx/busy/sqlite-busy).</summary>
    Retryable,

    /// <summary>Permanent failure; must NOT be retried (auth/policy/schema/validation/cancel/budget/4xx).</summary>
    NonRetryable,

    /// <summary>Write effect whose result is unknown; MUST NOT be auto-replayed (doc 04 §9).</summary>
    UnknownWriteOutcome
}

/// <summary>
/// Pure classifier mapping an <see cref="AppError"/> plus write-effect context to a
/// <see cref="RetryDisposition"/> (doc 04 §10). A write effect with an unknown outcome is always
/// <see cref="RetryDisposition.UnknownWriteOutcome"/> — never auto-replayed. Only the explicit
/// transient code whitelist (see <see cref="RunErrors.RetryableTransientCodes"/>) is retryable.
/// </summary>
public static class RetryClassifier
{
    public static RetryDisposition Classify(AppError? error, bool isWriteEffect, bool writeOutcomeUnknown)
    {
        if (isWriteEffect && writeOutcomeUnknown)
            return RetryDisposition.UnknownWriteOutcome;
        if (error is null)
            return RetryDisposition.NonRetryable;
        return RunErrors.IsRetryableTransientCode(error.Code)
            ? RetryDisposition.Retryable
            : RetryDisposition.NonRetryable;
    }
}
