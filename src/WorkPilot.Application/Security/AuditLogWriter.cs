using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Domain.Security;
using WorkPilot.Domain.Security.Audit;

namespace WorkPilot.Application.Security;

/// <summary>
/// Appends audit entries while maintaining the HMAC chain (SEC-106). Loads the last entry to derive
/// the predecessor hash and sequence, computes the linked entry via <see cref="AuditChain"/>, then
/// persists it. Never writes an entry whose chain would not verify.
/// </summary>
public sealed class AuditLogWriter
{
    private readonly IAuditLogStore _store;
    private readonly IAuditSigningKeyProvider _key;
    private readonly IClock _clock;

    public AuditLogWriter(IAuditLogStore store, IAuditSigningKeyProvider key, IClock clock)
    {
        _store = store;
        _key = key;
        _clock = clock;
    }

    public async Task<Result<AuditEntry>> AppendAsync(
        AuditCategory category,
        string action,
        string actor,
        string subjectJson,
        string decisionTraceJson,
        string safeDetailJson,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(action))
            return Result<AuditEntry>.Fail(SecurityErrors.EventInvalidError("action 必填"));

        var now = _clock.UtcNow;
        var previous = await _store.GetLastAsync(ct);
        var content = new AuditEntry(
            Sequence: 0,
            OccurredAtUtc: now,
            Category: category,
            Action: action,
            Actor: actor,
            SubjectJson: subjectJson,
            DecisionTraceJson: decisionTraceJson,
            SafeDetailJson: safeDetailJson,
            PrevHmac: string.Empty,
            Hmac: string.Empty,
            CreatedAtUtc: now);

        var linked = AuditChain.Link(_key.GetKey(), previous, content);
        return await _store.AppendAsync(linked, ct);
    }
}
