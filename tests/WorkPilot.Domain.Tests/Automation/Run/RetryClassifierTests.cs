using WorkPilot.Domain.Automation.Run;
using Xunit;

namespace WorkPilot.Domain.Tests.Automation.Run;

public class RetryClassifierTests
{
    [Fact]
    public void Retryable_transient_code_is_Retryable()
    {
        var err = RunErrors.TransientHttp5xxError("node_1");
        Assert.Equal(RetryDisposition.Retryable, RetryClassifier.Classify(err, isWriteEffect: false, writeOutcomeUnknown: false));
    }

    [Fact]
    public void Non_retryable_policy_error_is_NonRetryable()
    {
        var err = RunErrors.PermitEpochChangedError();
        Assert.Equal(RetryDisposition.NonRetryable, RetryClassifier.Classify(err, isWriteEffect: false, writeOutcomeUnknown: false));
    }

    [Fact]
    public void Null_error_is_NonRetryable()
        => Assert.Equal(RetryDisposition.NonRetryable, RetryClassifier.Classify(null, false, false));

    [Fact]
    public void Auth_or_budget_errors_are_never_retried()
    {
        Assert.Equal(RetryDisposition.NonRetryable, RetryClassifier.Classify(RunErrors.PermitEpochChangedError(), false, false));
        Assert.Equal(RetryDisposition.NonRetryable, RetryClassifier.Classify(RunErrors.CapabilityInvokeFailedError("n"), false, false));
    }

    [Fact]
    public void Write_effect_with_unknown_outcome_is_UnknownWriteOutcome_even_if_code_is_retryable()
    {
        // A transient code must NOT override the unknown-write-outcome rule (doc 04 §9/§10).
        var err = RunErrors.TransientHttp5xxError("node_1");
        Assert.Equal(RetryDisposition.UnknownWriteOutcome,
            RetryClassifier.Classify(err, isWriteEffect: true, writeOutcomeUnknown: true));
    }

    [Fact]
    public void Write_effect_with_known_failure_is_still_NonRetryable()
        => Assert.Equal(RetryDisposition.NonRetryable,
            RetryClassifier.Classify(RunErrors.CapabilityInvokeFailedError("n"), isWriteEffect: true, writeOutcomeUnknown: false));
}
