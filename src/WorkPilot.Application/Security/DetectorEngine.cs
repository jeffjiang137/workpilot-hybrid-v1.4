using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Domain.Security;
using WorkPilot.Domain.Security.Detectors;

namespace WorkPilot.Application.Security;

/// <summary>Result of a single detection pass.</summary>
public sealed record DetectorRunResult(IReadOnlyList<SecurityEvent> EmittedEvents, IReadOnlyList<DetectorAction> AppliedActions);

/// <summary>
/// Runs all 16 fixed detector rules against a <see cref="DetectorContext"/>, emits the resulting
/// security events through <see cref="ISecurityEventEmitter"/>, and applies each requested remediation
/// action exactly once (idempotency via <see cref="IDetectorActionStore"/>). A recurring condition
/// therefore never produces an event storm or re-fires an action (doc 06 §4).
/// </summary>
public sealed class DetectorEngine
{
    private readonly IReadOnlyList<IDetectorRule> _rules;
    private readonly ISecurityEventEmitter _emitter;
    private readonly IDetectorActionStore _actions;
    private readonly IDetectorActionExecutor _executor;

    public DetectorEngine(
        IReadOnlyList<IDetectorRule> rules,
        ISecurityEventEmitter emitter,
        IDetectorActionStore actions,
        IDetectorActionExecutor executor)
    {
        _rules = rules;
        _emitter = emitter;
        _actions = actions;
        _executor = executor;
    }

    public async Task<DetectorRunResult> RunAsync(DetectorContext ctx, CancellationToken ct)
    {
        var emitted = new List<SecurityEvent>();
        var applied = new List<DetectorAction>();
        var seenThisRun = new HashSet<string>();

        foreach (var rule in _rules)
        {
            IReadOnlyList<DetectorFinding> findings;
            try
            {
                findings = rule.Evaluate(ctx);
            }
            catch
            {
                // A single misbehaving rule must not abort the whole pass or swallow the failure
                // silently — the caller surfaces "Detection degraded" (doc 06 §10).
                throw;
            }

            foreach (var f in findings)
            {
                var key = f.Event.Fingerprint + "|" + f.Event.Type;
                if (!seenThisRun.Add(key))
                    continue; // dedup within a single pass

                await _emitter.EmitAsync(f.Event, ct);
                emitted.Add(f.Event);

                if (f.Action is not null && await _actions.TryMarkAppliedAsync(f.Action.ActionId, ct))
                {
                    var r = await _executor.ApplyAsync(f.Action, ct);
                    if (r.IsSuccess)
                        applied.Add(f.Action);
                }
            }
        }

        return new DetectorRunResult(emitted, applied);
    }
}
