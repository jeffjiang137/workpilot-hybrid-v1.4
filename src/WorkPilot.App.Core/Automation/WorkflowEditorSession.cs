using System.Linq;
using WorkPilot.App.Core.Primitives;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Domain.Automation;
using WorkPilot.Domain.Automation.Validation;

namespace WorkPilot.App.Core.Automation;

/// <summary>
/// Editable wrapper around a workflow DAG. Maintains an ordered node list and edge list, enforces the
/// node cap (AUT-A05: 32 nodes), rebuilds the immutable <see cref="WorkflowDefinition"/> on demand, and
/// re-runs the shared T05 <see cref="WorkflowValidator"/> so the editor never holds a second algorithm.
/// </summary>
public sealed class WorkflowEditorSession : ObservableBase
{
    private readonly List<WorkflowNode> _nodes = new();
    private readonly List<WorkflowEdge> _edges = new();
    private string? _entryNodeId;

    public WorkflowEditorSession(WorkflowDefinition? initial)
    {
        if (initial is null)
            return;
        _nodes.AddRange(initial.Nodes);
        _edges.AddRange(initial.Edges);
        _entryNodeId = initial.EntryNodeId;
    }

    public IReadOnlyList<WorkflowNode> Nodes => _nodes;
    public IReadOnlyList<WorkflowEdge> Edges => _edges;
    public string? EntryNodeId => _entryNodeId;

    public int NodeCount => _nodes.Count;
    public bool AtCapacity => _nodes.Count >= Limits.V1_5.MaxWorkflowNodes;

    /// <summary>Live validation using the single T05 validator.</summary>
    public ValidationResult Validation => WorkflowValidator.Validate(Build());

    /// <summary>Adds a node unless the 32-node cap is reached. Returns false when rejected.</summary>
    public bool TryAddNode(WorkflowNode node)
    {
        if (node is null || AtCapacity)
            return false;
        _nodes.Add(node);
        if (_entryNodeId is null)
            _entryNodeId = node.NodeId;
        RaiseAll();
        return true;
    }

    /// <summary>Removes a node and every edge touching it; re-points the entry if needed. Returns false if absent.</summary>
    public bool RemoveNode(string nodeId)
    {
        if (!_nodes.Any(n => n.NodeId == nodeId))
            return false;
        _nodes.RemoveAll(n => n.NodeId == nodeId);
        _edges.RemoveAll(e => e.FromNodeId == nodeId || e.ToNodeId == nodeId);
        if (_entryNodeId == nodeId)
            _entryNodeId = _nodes.Count > 0 ? _nodes[0].NodeId : null;
        RaiseAll();
        return true;
    }

    /// <summary>Moves a node one position earlier in the list (keyboard Alt+Up). Returns false at the top.</summary>
    public bool MoveUp(string nodeId)
    {
        var i = _nodes.FindIndex(n => n.NodeId == nodeId);
        if (i <= 0)
            return false;
        (_nodes[i - 1], _nodes[i]) = (_nodes[i], _nodes[i - 1]);
        RaiseAll();
        return true;
    }

    /// <summary>Moves a node one position later in the list (keyboard Alt+Down). Returns false at the bottom.</summary>
    public bool MoveDown(string nodeId)
    {
        var i = _nodes.FindIndex(n => n.NodeId == nodeId);
        if (i < 0 || i >= _nodes.Count - 1)
            return false;
        (_nodes[i + 1], _nodes[i]) = (_nodes[i], _nodes[i + 1]);
        RaiseAll();
        return true;
    }

    public void SetDisabled(string nodeId, bool disabled)
    {
        var i = _nodes.FindIndex(n => n.NodeId == nodeId);
        if (i < 0)
            return;
        _nodes[i] = _nodes[i] with { Disabled = disabled };
        Raise(nameof(Nodes));
    }

    public bool AddEdge(WorkflowEdge edge)
    {
        if (edge is null || _edges.Count >= Limits.V1_5.MaxWorkflowEdges)
            return false;
        _edges.Add(edge);
        Raise(nameof(Edges));
        return true;
    }

    public bool RemoveEdge(WorkflowEdge edge) =>
        RemoveEdge(edge.FromNodeId, edge.ToNodeId, edge.Branch);

    public bool RemoveEdge(string from, string to, string branch)
    {
        var removed = _edges.RemoveAll(e => e.FromNodeId == from && e.ToNodeId == to && e.Branch == branch) > 0;
        if (removed)
            Raise(nameof(Edges));
        return removed;
    }

    /// <summary>Rebuilds the immutable definition from the current working state.</summary>
    public WorkflowDefinition Build() => new(1, _entryNodeId ?? string.Empty, _nodes, _edges);

    private void RaiseAll()
    {
        Raise(nameof(Nodes));
        Raise(nameof(Edges));
        Raise(nameof(EntryNodeId));
        Raise(nameof(NodeCount));
        Raise(nameof(AtCapacity));
        Raise(nameof(Validation));
    }
}
