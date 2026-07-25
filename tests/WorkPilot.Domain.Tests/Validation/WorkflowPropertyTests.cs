using System;
using System.Collections.Generic;
using System.Linq;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Domain.Automation;
using WorkPilot.Domain.Automation.Validation;
using Xunit;

namespace WorkPilot.Domain.Tests.Validation;

/// <summary>
/// Property-style invariant checks for the workflow validator (spec doc 12). A seeded <see cref="Random"/>
/// drives the loop so failures are reproducible.
/// </summary>
public sealed class WorkflowPropertyTests
{
    [Fact]
    public void Linear_chain_of_agent_nodes_is_valid_for_every_size_up_to_32()
    {
        for (var n = 1; n <= Limits.V1_5.MaxWorkflowNodes; n++)
        {
            var nodes = new WorkflowNode[n];
            var edges = new WorkflowEdge[n - 1];
            for (var i = 0; i < n; i++)
            {
                nodes[i] = Wf.Agent($"n{i}", $"o{i}");
                if (i > 0) edges[i - 1] = Wf.Edge($"n{i - 1}", $"n{i}");
            }
            var wf = Wf.Of("n0", nodes, edges);
            var r = WorkflowValidator.Validate(wf);
            Assert.True(r.IsValid, $"linear chain of {n} nodes should be valid but got: {string.Join(",", r.Errors.Select(e => e.Code))}");
        }
    }

    [Fact]
    public void Any_back_edge_introduces_a_cycle_error()
    {
        var rand = new Random(7);
        for (var trial = 0; trial < 50; trial++)
        {
            var n = rand.Next(3, 20); // need >=3 so the back edge targets a non-entry node
            var nodes = Enumerable.Range(0, n).Select(i => Wf.Agent($"n{i}", $"o{i}")).ToArray();
            var edges = new List<WorkflowEdge>();
            for (var i = 1; i < n; i++) edges.Add(Wf.Edge($"n{i - 1}", $"n{i}"));
            // add a back edge from a later node to an earlier non-entry node -> cycle (entry stays in-degree 0)
            edges.Add(Wf.Edge($"n{n - 1}", "n1"));
            var wf = Wf.Of("n0", nodes, edges.ToArray());
            var r = WorkflowValidator.Validate(wf);
            Assert.Contains(r.Errors, e => e.Code == ValidationCodes.WorkflowCycle);
        }
    }
}
