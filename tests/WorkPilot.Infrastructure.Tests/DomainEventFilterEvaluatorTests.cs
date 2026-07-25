using System.Text.Json.Nodes;
using WorkPilot.Application.Automation.Materialization;
using Xunit;

namespace WorkPilot.Infrastructure.Tests;

/// <summary>spec doc 04 §4: pure domain-event filter matching (equals / in / fail-closed).</summary>
public class DomainEventFilterEvaluatorTests
{
    private static JsonObject Payload(string json) => JsonNode.Parse(json)!.AsObject();
    private static JsonArray Filters(string json) => JsonNode.Parse(json)!.AsArray();

    [Fact]
    public void Null_or_empty_filters_always_match()
    {
        Assert.True(DomainEventFilterEvaluator.Matches(null, Payload("{\"kind\":\"a\"}")));
        Assert.True(DomainEventFilterEvaluator.Matches(Filters("[]"), Payload("{\"kind\":\"a\"}")));
    }

    [Fact]
    public void Equals_clause_matches_when_value_equal()
        => Assert.True(DomainEventFilterEvaluator.Matches(
            Filters("[{\"field\":\"kind\",\"equals\":\"file.created\"}]"),
            Payload("{\"kind\":\"file.created\"}")));

    [Fact]
    public void Equals_clause_rejects_when_value_differs()
        => Assert.False(DomainEventFilterEvaluator.Matches(
            Filters("[{\"field\":\"kind\",\"equals\":\"file.created\"}]"),
            Payload("{\"kind\":\"file.deleted\"}")));

    [Fact]
    public void In_clause_matches_any_member()
        => Assert.True(DomainEventFilterEvaluator.Matches(
            Filters("[{\"field\":\"kind\",\"in\":[\"file.created\",\"file.updated\"]}]"),
            Payload("{\"kind\":\"file.updated\"}")));

    [Fact]
    public void In_clause_rejects_when_not_member()
        => Assert.False(DomainEventFilterEvaluator.Matches(
            Filters("[{\"field\":\"kind\",\"in\":[\"file.created\"]}]"),
            Payload("{\"kind\":\"file.deleted\"}")));

    [Fact]
    public void Missing_field_in_payload_rejects()
        => Assert.False(DomainEventFilterEvaluator.Matches(
            Filters("[{\"field\":\"kind\",\"equals\":\"x\"}]"),
            Payload("{\"other\":1}")));

    [Fact]
    public void Null_payload_with_filters_rejects()
        => Assert.False(DomainEventFilterEvaluator.Matches(Filters("[{\"field\":\"kind\",\"equals\":\"x\"}]"), null));

    [Fact]
    public void Malformed_clause_without_equals_or_in_fails_closed()
        => Assert.False(DomainEventFilterEvaluator.Matches(
            Filters("[{\"field\":\"kind\"}]"),
            Payload("{\"kind\":\"x\"}")));

    [Fact]
    public void All_clauses_must_pass()
        => Assert.False(DomainEventFilterEvaluator.Matches(
            Filters("[{\"field\":\"a\",\"equals\":\"1\"},{\"field\":\"b\",\"equals\":\"2\"}]"),
            Payload("{\"a\":\"1\",\"b\":\"9\"}")));
}
