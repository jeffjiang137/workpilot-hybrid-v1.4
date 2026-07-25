namespace WorkPilot.Domain.Security.Audit;

/// <summary>
/// One append-only, tamper-evident security audit entry (SEC-106). Each entry is cryptographically
/// linked to its predecessor via <see cref="PrevHmac"/> and protected by <see cref="Hmac"/>
/// (HMAC-SHA256 over <c>prev_hmac || canonical_payload</c>). The entry contains only safe,
/// display-name-free data and — for governance actions — an optional decision trace (doc 06 §8).
/// </summary>
public sealed record AuditEntry(
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    AuditCategory Category,
    string Action,
    string Actor,
    string SubjectJson,
    string DecisionTraceJson,
    string SafeDetailJson,
    string PrevHmac,
    string Hmac,
    DateTimeOffset CreatedAtUtc);
