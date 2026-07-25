using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Security;
using WorkPilot.Domain.Security.Audit;

namespace WorkPilot.Application.Security;

/// <summary>
/// Exposes whether a verified audit-break is currently blocking external capabilities (DoD:
/// "Audit 失败阻断外部能力"). The gate / adapter consults this before sending any external side effect.
/// </summary>
public interface IAuditIntegrityMonitor
{
    bool ExternalCapabilityBlocked { get; }

    /// <summary>Re-verifies the HMAC chain over the full audit log (SEC-106 / support package integrity).</summary>
    Task<AuditIntegrityReport> VerifyAsync(CancellationToken ct);
}

/// <summary>
/// Verifies the security audit HMAC chain. On any break it raises a Critical
/// <see cref="SecurityEventType.AuditIntegrityFailure"/> (DET-008) event and latches
/// <see cref="ExternalCapabilityBlocked"/> so the adapter refuses further unattended external calls
/// until the chain is restored and re-verified. This is tamper-EVIDENT detection, not prevention:
/// it stops accidental corruption / unsophisticated tampering from silently passing.
/// </summary>
public sealed class AuditIntegrityService : IAuditIntegrityMonitor
{
    private const string DetectorVersion = "1.0.0";

    private readonly IAuditLogStore _store;
    private readonly IAuditSigningKeyProvider _key;
    private readonly ISecurityEventEmitter? _emitter;
    private readonly IClock _clock;
    private int _blocked;

    public AuditIntegrityService(
        IAuditLogStore store,
        IAuditSigningKeyProvider key,
        IClock clock,
        ISecurityEventEmitter? emitter = null)
    {
        _store = store;
        _key = key;
        _clock = clock;
        _emitter = emitter;
    }

    public bool ExternalCapabilityBlocked => _blocked != 0;

    public async Task<AuditIntegrityReport> VerifyAsync(CancellationToken ct)
    {
        var entries = await _store.GetAllAsync(ct);
        var report = AuditIntegrity.Verify(entries, _key.GetKey());

        if (!report.Intact)
        {
            // Latch the block; external capabilities must stop until re-verified intact.
            Interlocked.Exchange(ref _blocked, 1);

            if (_emitter is not null)
            {
                var ev = BuildDet008(report.Error ?? "audit integrity unknown");
                await _emitter.EmitAsync(ev, ct);
            }
        }
        else
        {
            Interlocked.Exchange(ref _blocked, 0);
        }

        return report;
    }

    /// <summary>Explicitly clears the latch (e.g. after a successful restore + re-verify).</summary>
    public void ResetBlock() => Interlocked.Exchange(ref _blocked, 0);

    private SecurityEvent BuildDet008(string detail)
    {
        var now = _clock.UtcNow;
        var evidence = new Dictionary<string, string>
        {
            ["primary_error_code"] = "AUDIT_INTEGRITY",
            ["detail"] = detail.Length > 400 ? detail.Substring(0, 400) : detail
        };
        var fp = SecurityEventFingerprint.Compute(SecurityEventType.AuditIntegrityFailure, null, null, null, "AUDIT_INTEGRITY");
        return new SecurityEvent(
            SecurityEventId.Create(new SequentialDetectorId()), now, SecurityEventType.AuditIntegrityFailure,
            SecuritySeverity.Critical, fp, null, null, null, evidence, DetectorVersion);
    }

    private sealed class SequentialDetectorId : WorkPilot.Contracts.Primitives.IIdGenerator
    {
        private int _n;
        public string NewId() => $"det_{++_n}";
    }
}
