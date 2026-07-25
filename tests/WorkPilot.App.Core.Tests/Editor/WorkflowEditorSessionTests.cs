using System.Linq;
using WorkPilot.App.Core.Automation;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Domain.Automation;
using Xunit;

namespace WorkPilot.App.Core.Tests.Editor;

public class WorkflowEditorSessionTests
{
    private static WorkflowNode ValidNode(string id) =>
        new(id, "Node " + id, "agent_prompt", Limits.V1_5.MinWorkflowNodeTimeoutSeconds, false, null);

    [Fact]
    public void Empty_workflow_is_invalid_and_not_at_capacity()
    {
        var s = new WorkflowEditorSession(null);
        Assert.True(s.Validation.HasErrors);
        Assert.False(s.AtCapacity);
    }

    [Fact]
    public void Single_valid_node_is_valid()
    {
        var s = new WorkflowEditorSession(null);
        Assert.True(s.TryAddNode(ValidNode("n1")));
        Assert.False(s.Validation.HasErrors);
        Assert.Equal("n1", s.EntryNodeId);
    }

    [Fact]
    public void Node_cap_of_32_is_enforced()
    {
        var s = new WorkflowEditorSession(null);
        for (var i = 0; i < Limits.V1_5.MaxWorkflowNodes; i++)
            Assert.True(s.TryAddNode(ValidNode("n" + i)));

        Assert.True(s.AtCapacity);
        Assert.Equal(Limits.V1_5.MaxWorkflowNodes, s.NodeCount);

        // 33rd node is rejected; count stays at the cap.
        var rejected = s.TryAddNode(ValidNode("overflow"));
        Assert.False(rejected);
        Assert.Equal(Limits.V1_5.MaxWorkflowNodes, s.NodeCount);
    }

    [Fact]
    public void Remove_node_repoints_entry_when_needed()
    {
        var s = new WorkflowEditorSession(null);
        s.TryAddNode(ValidNode("a"));
        s.TryAddNode(ValidNode("b"));
        Assert.Equal("a", s.EntryNodeId);

        Assert.True(s.RemoveNode("a"));
        Assert.Equal("b", s.EntryNodeId);
        Assert.Equal(1, s.NodeCount);
    }

    [Fact]
    public void Move_up_and_down_reorders_list()
    {
        var s = new WorkflowEditorSession(null);
        s.TryAddNode(ValidNode("a"));
        s.TryAddNode(ValidNode("b"));
        s.TryAddNode(ValidNode("c"));
        Assert.Equal(new[] { "a", "b", "c" }, s.Nodes.Select(n => n.NodeId).ToArray());

        Assert.True(s.MoveDown("a"));
        Assert.Equal(new[] { "b", "a", "c" }, s.Nodes.Select(n => n.NodeId).ToArray());

        Assert.True(s.MoveUp("a"));
        Assert.Equal(new[] { "a", "b", "c" }, s.Nodes.Select(n => n.NodeId).ToArray());

        Assert.False(s.MoveUp("a")); // already at top
        Assert.False(s.MoveDown("c")); // already at bottom
    }

    [Fact]
    public void Cycle_is_detected()
    {
        // Entry 'a' has in-degree 0 (so we pass the entry-in-degree guard and reach Kahn detection),
        // but 'b' and 'c' form a reachable cycle b -> c -> b.
        var s = new WorkflowEditorSession(null);
        s.TryAddNode(ValidNode("a"));
        s.TryAddNode(ValidNode("b"));
        s.TryAddNode(ValidNode("c"));
        s.AddEdge(new WorkflowEdge("a", "b", "next"));
        s.AddEdge(new WorkflowEdge("b", "c", "next"));
        s.AddEdge(new WorkflowEdge("c", "b", "next")); // back-edge => cycle b->c->b
        Assert.Contains(s.Validation.Errors, e => e.Code == "WORKFLOW_CYCLE");
    }

    [Fact]
    public void Disabled_node_is_excluded_from_cycle_check()
    {
        var s = new WorkflowEditorSession(null);
        s.TryAddNode(ValidNode("a"));
        s.TryAddNode(ValidNode("b"));
        s.TryAddNode(ValidNode("c"));
        s.AddEdge(new WorkflowEdge("a", "b", "next"));
        s.AddEdge(new WorkflowEdge("b", "c", "next"));
        s.AddEdge(new WorkflowEdge("c", "b", "next"));
        Assert.Contains(s.Validation.Errors, e => e.Code == "WORKFLOW_CYCLE");

        s.SetDisabled("c", true);
        // The back-edge from a disabled node is no longer in the enabled graph, so the cycle dissolves.
        Assert.DoesNotContain(s.Validation.Errors, e => e.Code == "WORKFLOW_CYCLE");
    }
}
