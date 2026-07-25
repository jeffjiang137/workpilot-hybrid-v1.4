using System.Linq;
using WorkPilot.Domain.Automation;
using WorkPilot.Domain.Automation.Validation;
using Xunit;

namespace WorkPilot.Domain.Tests.Validation;

public sealed class WorkflowValidatorTests
{
    [Fact]
    public void Single_node_workflow_is_valid()
    {
        var wf = Wf.Of("a", Wf.Agent("a", "x"));
        var r = WorkflowValidator.Validate(wf);
        Assert.True(r.IsValid);
        Assert.Empty(r.Errors);
    }

    // AUT-A04: a cycle is detected. Uses a cycle not involving the entry (entry must keep in-degree 0).
    [Fact]
    public void Cycle_is_detected()
    {
        var wf = Wf.Of("a",
            new[] { Wf.Agent("a", "x"), Wf.Agent("b", "y"), Wf.Agent("c", "z") },
            new[] { Wf.Edge("a", "b"), Wf.Edge("b", "c"), Wf.Edge("c", "b") });
        var r = WorkflowValidator.Validate(wf);
        Assert.True(r.HasErrors);
        Assert.Contains(r.Errors, e => e.Code == ValidationCodes.WorkflowCycle);
    }

    // AUT-A04: an enabled node not reachable from the entry is an error.
    [Fact]
    public void Unreachable_node_is_detected()
    {
        var wf = Wf.Of("a",
            new[] { Wf.Agent("a", "x"), Wf.Agent("b", "y") },
            System.Array.Empty<WorkflowEdge>()); // b is isolated (entry 'a' has no edges)
        var r = WorkflowValidator.Validate(wf);
        Assert.Contains(r.Errors, e =>
            e.Code == ValidationCodes.WorkflowUnreachable &&
            e.SafeDetails?["node_id"] == "b");
    }

    // AUT-A04: non-condition node with >1 out-edge is invalid.
    [Fact]
    public void Out_degree_over_one_is_invalid()
    {
        var wf = Wf.Of("a",
            new[] { Wf.Agent("a", "x"), Wf.Agent("b", "y"), Wf.Agent("c", "z") },
            new[] { Wf.Edge("a", "b"), Wf.Edge("a", "c") });
        var r = WorkflowValidator.Validate(wf);
        Assert.Contains(r.Errors, e => e.Code == ValidationCodes.NodeOutDegreeInvalid);
    }

    // AUT-A04: condition must have exactly two branches true/false.
    [Fact]
    public void Condition_with_single_branch_is_invalid()
    {
        var wf = Wf.Of("a",
            new[] { Wf.Agent("a", "x"), Wf.ConditionNode("c", Wf.Condition(("vars.x", "exists"))), Wf.Agent("b", "y") },
            new[] { Wf.Edge("a", "c"), Wf.Edge("c", "b", "true") });
        var r = WorkflowValidator.Validate(wf);
        Assert.Contains(r.Errors, e => e.Code == ValidationCodes.ConditionBranchInvalid);
    }

    // AUT-A05: more than 32 nodes exceeds the bound.
    [Fact]
    public void More_than_32_nodes_exceeds_bound()
    {
        var nodes = Enumerable.Range(0, 33).Select(i => Wf.Agent($"n{i}", $"o{i}")).ToArray();
        var wf = Wf.Of("n0", nodes);
        var r = WorkflowValidator.Validate(wf);
        Assert.Contains(r.Errors, e => e.Code == ValidationCodes.NodeCountExceeded);
    }

    [Fact]
    public void Exactly_32_nodes_is_within_bound()
    {
        var nodes = Enumerable.Range(0, 32).Select(i => Wf.Agent($"n{i}", $"o{i}")).ToArray();
        var wf = Wf.Of("n0", nodes);
        var r = WorkflowValidator.Validate(wf);
        Assert.DoesNotContain(r.Errors, e => e.Code == ValidationCodes.NodeCountExceeded);
    }

    [Fact]
    public void Empty_workflow_is_invalid()
    {
        var wf = Wf.Of("a", System.Array.Empty<WorkflowNode>());
        var r = WorkflowValidator.Validate(wf);
        Assert.Contains(r.Errors, e => e.Code == ValidationCodes.WorkflowEmpty);
    }

    // AUT-A06: referencing a variable not produced upstream is invalid.
    [Fact]
    public void Reference_to_unproduced_variable_is_invalid()
    {
        var wf = Wf.Of("a",
            new[] { Wf.Agent("a", "x"), Wf.Agent("b", null, Wf.Bindings(("in", "vars.b"))) },
            new[] { Wf.Edge("a", "b") });
        var r = WorkflowValidator.Validate(wf);
        Assert.Contains(r.Errors, e => e.Code == ValidationCodes.VariableNotAvailable);
    }

    // AUT-A06: referencing a variable produced by an upstream node is valid.
    [Fact]
    public void Reference_to_upstream_variable_is_valid()
    {
        var wf = Wf.Of("a",
            new[] { Wf.Agent("a", "x"), Wf.Agent("b", null, Wf.Bindings(("in", "vars.x"))) },
            new[] { Wf.Edge("a", "b") });
        var r = WorkflowValidator.Validate(wf);
        Assert.DoesNotContain(r.Errors, e => e.Code == ValidationCodes.VariableNotAvailable);
    }

    // AUT-A06: a variable produced on one condition branch is NOT available on the other branch.
    [Fact]
    public void Cross_branch_variable_reference_is_invalid()
    {
        var wf = Wf.Of("a",
            new[]
            {
                Wf.Agent("a", "x"),
                Wf.ConditionNode("c", Wf.Condition(("vars.x", "exists"))),
                Wf.Agent("t", "branchVar"),   // produces branchVar on true branch
                Wf.Agent("f"),                 // false branch, no output
                Wf.Agent("consumer", null, Wf.Bindings(("in", "vars.branchVar")))
            },
            new[]
            {
                Wf.Edge("a", "c"),
                Wf.Edge("c", "t", "true"),
                Wf.Edge("c", "f", "false"),
                Wf.Edge("f", "consumer")       // consumer only reachable from false branch
            });
        var r = WorkflowValidator.Validate(wf);
        Assert.Contains(r.Errors, e => e.Code == ValidationCodes.VariableNotAvailable);
    }

    // Node id / display-name / timeout validation.
    [Fact]
    public void Invalid_node_id_is_detected()
    {
        var wf = Wf.Of("A1", Wf.Agent("A1", "x")); // uppercase id violates ^[a-z][a-z0-9_]$
        var r = WorkflowValidator.Validate(wf);
        Assert.Contains(r.Errors, e => e.Code == ValidationCodes.NodeIdInvalid);
    }

    [Fact]
    public void Timeout_out_of_range_is_detected()
    {
        var wf = Wf.Of("a", Wf.Agent("a", "x", timeout: 3)); // below MinWorkflowNodeTimeoutSeconds(5)
        var r = WorkflowValidator.Validate(wf);
        Assert.Contains(r.Errors, e => e.Code == ValidationCodes.NodeTimeoutInvalid);
    }

    [Fact]
    public void Output_key_must_not_be_reserved()
    {
        var wf = Wf.Of("a", Wf.Agent("a", "trigger")); // reserved root
        var r = WorkflowValidator.Validate(wf);
        Assert.Contains(r.Errors, e => e.Code == ValidationCodes.VariableOutputKeyInvalid);
    }
}
