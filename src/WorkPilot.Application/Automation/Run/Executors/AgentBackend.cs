using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace WorkPilot.Application.Automation.Run.Executors;

/// <summary>
/// A single resolved input passed to the agent backend. Values are safe trigger/run/system/vars data;
/// secrets are never present (the <see cref="VariableStore"/> rejects the <c>secrets</c> root).
/// </summary>
public sealed record AgentInputVariable(string Name, JsonNode? Value);

/// <summary>
/// Request to run a frozen Expert against a structured context, produced by the <see cref="AgentExecutor"/>
/// from an <c>agent_prompt</c> node (doc 03 §3.2). The real backend (model client + capability scoping)
/// lives in the Host/Infrastructure layer; tests use a scripted fake.
/// </summary>
public sealed record AgentInvocationRequest(
    string NodeId,
    string Instruction,
    IReadOnlyList<AgentInputVariable> Inputs,
    int MaxModelTurns,
    string CapabilityMode,
    IReadOnlyList<string> DeclaredCapabilities);

/// <summary>Outcome of a single agent invocation.</summary>
public sealed record AgentInvocationResult(
    bool IsSuccess,
    JsonNode? OutputValue = null,
    string? ErrorCode = null,
    int ModelTurnsConsumed = 0);

/// <summary>
/// Port to the model/agent backend. T11 defines only the contract so the pure <see cref="AgentExecutor"/>
/// is exercisable without any model dependency; the production implementation (frozen Expert + capability
/// scoping, T12/T17) is supplied by the Host layer. Implementations must honour <paramref name="ct"/> and
/// throw <see cref="System.OperationCanceledException"/> when cancelled so the run is marked Cancelled.
/// </summary>
public interface IAgentBackend
{
    Task<AgentInvocationResult> InvokeAsync(AgentInvocationRequest request, CancellationToken ct);
}
