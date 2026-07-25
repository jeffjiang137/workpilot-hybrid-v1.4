using System.Collections.Generic;
using System.Text.Json.Nodes;
using WorkPilot.Domain.Automation;
using WorkPilot.Domain.Automation.Run;
using WorkPilot.Domain.Automation.Run.Interpreter;
using Xunit;

namespace WorkPilot.Domain.Tests.Automation.Run;

/// <summary>Variable scope + $ref resolution (doc 03 §4). Secrets never enter the store.</summary>
public class VariableStoreTests
{
    private static VariableStore Store() => new(
        triggerVars: new Dictionary<string, JsonNode>
        {
            ["project"] = new JsonObject { ["owner"] = "alice", ["priority"] = 5 }
        },
        runVars: new Dictionary<string, JsonNode> { ["id"] = "run_1" });

    [Fact]
    public void Resolves_trigger_nested_path()
    {
        var s = Store();
        Assert.True(s.TryResolve("trigger.project.owner", out var v));
        Assert.Equal("alice", v!.GetValue<string>());
    }

    [Fact]
    public void Resolves_run_root_path()
    {
        var s = Store();
        Assert.True(s.TryResolve("run.id", out var v));
        Assert.Equal("run_1", v!.GetValue<string>());
    }

    [Fact]
    public void Declared_variable_is_resolvable()
    {
        var s = Store();
        s.Declare("n1", "summary", JsonValue.Create("done"));
        Assert.Contains("summary", s.DeclaredKeys);
        Assert.True(s.TryResolve("vars.summary", out var v));
        Assert.Equal("done", v!.GetValue<string>());
    }

    [Fact]
    public void Missing_path_returns_false_without_throwing()
    {
        var s = Store();
        Assert.False(s.TryResolve("trigger.project.missing", out var v));
        Assert.Null(v);
        Assert.False(s.TryResolve("vars.nope", out _));
    }

    [Fact]
    public void Secrets_root_never_resolves()
    {
        var s = Store();
        Assert.False(s.TryResolve("secrets.api_key", out _));
    }

    [Theory]
    [InlineData("run")]      // reserved
    [InlineData("trigger")]  // reserved
    [InlineData("secrets")]  // reserved
    [InlineData("Bad")]      // must start lowercase
    [InlineData("has-dash")] // only [a-z0-9_]
    [InlineData("")]         // empty
    public void Declare_rejects_reserved_or_malformed_keys(string key)
    {
        var s = Store();
        Assert.Throws<DomainException>(() => s.Declare("n1", key, JsonValue.Create(1)));
    }

    [Fact]
    public void Resolution_returns_a_deep_clone_not_the_stored_node()
    {
        var s = Store();
        s.TryResolve("trigger.project", out var first);
        ((JsonObject)first!)["owner"] = "mutated";
        s.TryResolve("trigger.project", out var second);
        Assert.Equal("alice", ((JsonObject)second!)["owner"]!.GetValue<string>());
    }
}
