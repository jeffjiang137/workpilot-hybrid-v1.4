using System;
using System.Threading;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Domain.Automation;
using WorkPilot.Domain.Automation.Run;
using WorkPilot.Domain.Automation.Run.Interpreter;
using WorkPilot.Application.Automation.Run.Permit;

namespace WorkPilot.Application.Automation.Run.Executors;

/// <summary>
/// Application-layer dispatcher implementing the Domain <see cref="INodeEffectExecutor"/> port (T11).
/// Routes each node kind to its concrete executor. Condition nodes are evaluated inline by the
/// interpreter and never reach this port. <c>capability_call</c> / <c>approval</c> are owned by T12/T13;
/// <c>capability_call</c> is now executed with the Native single-use Permit threaded through the adapter;
/// <c>approval</c> still routes to a closed-world <see cref="StepRunStatus.BlockedPolicy"/> until T13.
/// </summary>
public sealed class NodeEffectExecutor : INodeEffectExecutor
{
    private readonly AgentExecutor _agent;
    private readonly DelayExecutor _delay;
    private readonly NotificationExecutor _notification;
    private readonly CapabilityExecutor? _capability;

    public NodeEffectExecutor(
        IAgentBackend agentBackend,
        INotificationSink notificationSink,
        IPermitIssuer? permitIssuer = null,
        ICapabilityAdapterResolver? adapterResolver = null,
        ISideEffectJournal? journal = null,
        IClock? clock = null,
        Func<long>? revocationEpochProvider = null)
    {
        _agent = new AgentExecutor(agentBackend);
        _delay = new DelayExecutor();
        _notification = new NotificationExecutor(notificationSink);
        _capability = (permitIssuer, adapterResolver, journal, clock) switch
        {
            (not null, not null, not null, not null) => new CapabilityExecutor(permitIssuer, adapterResolver, journal, clock, revocationEpochProvider),
            _ => null
        };
    }

    public NodeCost Estimate(WorkflowNode node) => node.Kind switch
    {
        "agent_prompt" => _agent.Estimate(node),
        "delay" => _delay.Estimate(node),
        "notification" => _notification.Estimate(node),
        "capability_call" or "approval" => new NodeCost(0, 1, 4096, 0), // one capability call, reserved up-front
        _ => throw new DomainException(RunErrors.NodeKindNotSupportedError(node.Kind))
    };

    public NodeEffectResult ExecuteNode(WorkflowNode node, VariableStore inputVars, AutomationRun run, StepRun step, CancellationToken ct)
    {
        switch (node.Kind)
        {
            case "agent_prompt":
                return _agent.ExecuteNode(node, inputVars, run, step, ct);
            case "delay":
                return _delay.ExecuteNode(node, inputVars, run, step, ct);
            case "notification":
                return _notification.ExecuteNode(node, inputVars, run, step, ct);
            case "capability_call":
                if (_capability is null)
                    return new NodeEffectResult(StepRunStatus.BlockedPolicy,
                        ErrorCode: RunErrors.NodeKindNotSupportedError(node.Kind).Code);
                return _capability.ExecuteNode(node, inputVars, run, step, ct);
            case "approval":
                return new NodeEffectResult(StepRunStatus.BlockedPolicy,
                    ErrorCode: RunErrors.NodeKindNotSupportedError(node.Kind).Code);
            default:
                throw new DomainException(RunErrors.NodeKindNotSupportedError(node.Kind));
        }
    }
}
