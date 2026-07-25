using System;
using System.Text.Json;
using WorkPilot.Contracts.Primitives;
using Xunit;

namespace WorkPilot.Contracts.Tests;

public sealed class ResultErrorTests
{
    private sealed class SampleCatalog : FeatureErrorCatalog
    {
        private readonly string _feature;
        private readonly System.Collections.Generic.IReadOnlyList<ErrorDefinition> _defs;

        public SampleCatalog(string feature, params ErrorDefinition[] defs)
        {
            _feature = feature;
            _defs = defs;
        }

        public override string Feature => _feature;
        public override System.Collections.Generic.IReadOnlyList<ErrorDefinition> Definitions => _defs;
    }

    [Fact]
    public void Result_Ok_and_Fail_carry_state()
    {
        var ok = Result<int>.Ok(42);
        Assert.True(ok.IsSuccess);
        Assert.Equal(42, ok.Value);

        var err = new AppError("E1", ErrorCategory.Validation, "msg.E1", false);
        var fail = Result<int>.Fail(err);
        Assert.False(fail.IsSuccess);
        Assert.Equal("E1", fail.Error!.Code);
    }

    [Fact]
    public void Result_implicit_conversions()
    {
        Result<int> fromValue = 7;
        Assert.True(fromValue.IsSuccess);
        Assert.Equal(7, fromValue.Value);

        Result<int> fromError = new AppError("X", ErrorCategory.Internal, "m", false);
        Assert.False(fromError.IsSuccess);
    }

    [Fact]
    public void Result_map_bind_match()
    {
        var doubled = Result<int>.Ok(2).Map(x => x * 2);
        Assert.Equal(4, doubled.Value);

        var bound = Result<int>.Ok(2).Bind(x => Result<int>.Ok(x + 1));
        Assert.Equal(3, bound.Value);

        var msg = Result<int>.Ok(5).Match(s => $"ok:{s}", e => "err");
        Assert.Equal("ok:5", msg);

        var tapped = false;
        Result<int>.Ok(1).Tap(_ => tapped = true);
        Assert.True(tapped);
    }

    [Fact]
    public void AppError_is_value_equal_by_fields()
    {
        var a = new AppError("C", ErrorCategory.Policy, "k", true);
        var b = new AppError("C", ErrorCategory.Policy, "k", true);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void AppError_safe_details_default_to_empty()
    {
        var e = new AppError("C", ErrorCategory.Auth, "k", false);
        Assert.NotNull(e.SafeDetails);
        Assert.Empty(e.SafeDetails);
        Assert.Equal(string.Empty, e.CorrelationId);
    }

    [Fact]
    public void AppError_serializes_to_json_with_its_code()
    {
        var e = new AppError("PER_006", ErrorCategory.Policy, "policy.denied", false, correlationId: "corr-1");
        var json = JsonSerializer.Serialize(e);
        Assert.Contains("PER_006", json);
        Assert.Contains("policy.denied", json);
    }

    [Fact]
    public void ErrorCatalog_rejects_duplicate_code_across_features()
    {
        ErrorCatalog.Register(new SampleCatalog("DupA", new ErrorDefinition("DC_A1", ErrorCategory.Validation, "m", false)));
        var dup = new SampleCatalog("DupB", new ErrorDefinition("DC_A1", ErrorCategory.Policy, "m2", true));
        Assert.Throws<InvalidOperationException>(() => ErrorCatalog.Register(dup));
    }

    [Fact]
    public void ErrorCatalog_allows_distinct_codes_across_features()
    {
        ErrorCatalog.Register(new SampleCatalog("DistA", new ErrorDefinition("XA_1", ErrorCategory.Validation, "a", false), new ErrorDefinition("XA_2", ErrorCategory.Conflict, "b", true)));
        ErrorCatalog.Register(new SampleCatalog("DistB", new ErrorDefinition("XB_1", ErrorCategory.Policy, "c", false)));
    }

    [Fact]
    public void ErrorCatalog_exposes_all_codes_and_uniqueness()
    {
        ErrorCatalog.Register(new SampleCatalog("AllA", new ErrorDefinition("AC_1", ErrorCategory.Database, "d", true), new ErrorDefinition("AC_2", ErrorCategory.Network, "e", false)));
        Assert.Contains("AC_1", ErrorCatalog.AllCodes());
        Assert.Contains("AC_2", ErrorCatalog.AllCodes());
        Assert.False(ErrorCatalog.IsCodeUnique("AC_1"));
        Assert.True(ErrorCatalog.IsCodeUnique("never_used_code"));
    }
}
