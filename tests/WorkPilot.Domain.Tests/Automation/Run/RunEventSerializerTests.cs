using System.Collections.Generic;
using WorkPilot.Domain.Automation.Run;
using Xunit;

namespace WorkPilot.Domain.Tests.Automation.Run;

public class RunEventSerializerTests
{
    [Fact]
    public void CapabilityCompleted_serializes_only_allowlist_keys_LOG_A01()
    {
        var props = new Dictionary<string, object?>
        {
            ["source_id"] = "connector:acct_1",
            ["capability_stable_id"] = "send_email",
            ["risk"] = "Medium",
            ["duration_ms"] = 1234L,
            ["item_count"] = 2L,
            ["result_size_bucket"] = "small",
            ["truncated"] = false
        };

        var result = RunEventSerializer.Serialize("capability_completed", props);

        Assert.True(result.IsSuccess);
        var json = result.Value!;
        Assert.Contains("\"source_id\":\"connector:acct_1\"", json);
        Assert.Contains("\"capability_stable_id\":\"send_email\"", json);
        Assert.Contains("\"risk\":\"Medium\"", json);
        Assert.Contains("\"duration_ms\":1234", json);
        Assert.Contains("\"item_count\":2", json);
        Assert.Contains("\"result_size_bucket\":\"small\"", json);
        Assert.Contains("\"truncated\":false", json);
        Assert.StartsWith("{", json);
        Assert.EndsWith("}", json);
    }

    [Fact]
    public void Unknown_key_is_rejected_LOG_A02()
    {
        var props = new Dictionary<string, object?>
        {
            ["source_id"] = "connector:acct_1",
            ["prompt_body"] = "do not log me" // not in allowlist
        };

        var result = RunEventSerializer.Serialize("capability_completed", props);

        Assert.False(result.IsSuccess);
        Assert.Equal("RUN_EVENT_CONTRACT_VIOLATION", result.Error!.Code);
    }

    [Fact]
    public void Unknown_kind_is_rejected()
    {
        var props = new Dictionary<string, object?> { ["x"] = "y" };
        var result = RunEventSerializer.Serialize("no_such_kind", props);
        Assert.False(result.IsSuccess);
        Assert.Equal("RUN_EVENT_CONTRACT_VIOLATION", result.Error!.Code);
    }

    [Fact]
    public void Non_string_value_for_string_field_is_rejected()
    {
        var props = new Dictionary<string, object?> { ["risk"] = 42 }; // risk must be enum string
        var result = RunEventSerializer.Serialize("capability_completed", props);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Bad_risk_enum_is_rejected()
    {
        var props = new Dictionary<string, object?> { ["risk"] = "CRITICAL" }; // must be Critical, not uppercase
        var result = RunEventSerializer.Serialize("capability_completed", props);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Out_of_range_number_is_rejected()
    {
        var props = new Dictionary<string, object?> { ["duration_ms"] = 9_000_000L }; // > 3_600_000
        var result = RunEventSerializer.Serialize("capability_completed", props);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void String_value_over_maxlength_is_rejected()
    {
        var props = new Dictionary<string, object?> { ["source_id"] = new string('a', 300) }; // stable id max 128, spec max 256
        var result = RunEventSerializer.Serialize("capability_completed", props);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Empty_properties_serializes_to_empty_object()
    {
        var result = RunEventSerializer.Serialize("lease_acquired", new Dictionary<string, object?>());
        Assert.True(result.IsSuccess);
        Assert.Equal("{}", result.Value);
    }

    [Fact]
    public void All_catalog_kinds_have_at_least_one_property()
    {
        foreach (var kind in RunEventCatalog.KnownKinds)
            Assert.True(RunEventCatalog.TryGet(kind, out _));
    }
}
