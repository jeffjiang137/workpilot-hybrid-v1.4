using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Application.Automation;
using WorkPilot.Application.Permission.Policy;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Domain.Automation;
using WorkPilot.Domain.Security.Audit;

namespace WorkPilot.Application.Security.Governance;

/// <summary>
/// Emergency stop orchestration (doc 06 §6.4). A single logical transaction:
/// 1. set <c>security_state.emergency_stop = true</c>;
/// 2. bump the global revocation epoch (every prior permit/receipt/grant fails Current-State Check);
/// 3. pause every <c>Enabled</c> automation (saving prior state);
/// 4. write a Critical governance audit entry.
/// Recovery is <b>not</b> one-click: <see cref="ResumeAsync"/> only clears the flag and writes an
/// audit entry — the operator must then verify the audit trail and sources and selectively resume
/// automations (doc 06 §6.4). The host's adapter reads/caches the epoch with a ≤1s TTL so the bump
/// propagates to in-flight sends.
/// </summary>
public sealed class EmergencyStopCoordinator
{
    private const string EmergencyStopKey = "emergency_stop";

    private readonly ISecurityStateStore _state;
    private readonly IRevocationEpoch _epoch;
    private readonly IAutomationRepository _automations;
    private readonly AuditLogWriter _audit;

    public EmergencyStopCoordinator(
        ISecurityStateStore state,
        IRevocationEpoch epoch,
        IAutomationRepository automations,
        AuditLogWriter audit)
    {
        _state = state;
        _epoch = epoch;
        _automations = automations;
        _audit = audit;
    }

    public async Task<Result> StopAsync(string actor, CancellationToken ct)
    {
        var existing = await _state.GetAsync(EmergencyStopKey, ct);
        if (existing.IsSuccess && existing.Value is "true")
            return Result.Failure(SecurityGovernanceErrors.EmergencyStopActiveError());

        await _state.SetAsync(EmergencyStopKey, "true", ct);
        _epoch.Bump();

        var enabled = await _automations.ListEnabledAsync(ct);
        if (enabled.IsSuccess && enabled.Value is not null)
        {
            foreach (var definition in enabled.Value)
            {
                var paused = definition.Pause();
                if (paused.IsSuccess) await _automations.SaveAsync(definition, null, ct);
            }
        }

        await _audit.AppendAsync(
            AuditCategory.Governance, "emergency_stop", actor,
            "{\"scope\":\"all\"}", "{\"decision\":\"all enabled automations paused; global revocation epoch bumped\"}",
            "{\"detail\":\"critical emergency stop activated\"}", ct);
        return Result.Success();
    }

    public async Task<Result> ResumeAsync(string actor, CancellationToken ct)
    {
        var existing = await _state.GetAsync(EmergencyStopKey, ct);
        if (!(existing.IsSuccess && existing.Value is "true"))
            return Result.Success(); // already not in emergency stop — idempotent

        await _state.SetAsync(EmergencyStopKey, "false", ct);
        await _audit.AppendAsync(
            AuditCategory.Governance, "emergency_stop_lifted", actor,
            "{\"scope\":\"all\"}", "{}",
            "{\"detail\":\"emergency stop lifted; manual per-automation recovery required\"}", ct);
        return Result.Success();
    }
}
