using System.Security.Cryptography;
using System.Text;

namespace WorkPilot.Domain.Security.Audit;

/// <summary>
/// Pure HMAC chaining for the security audit log (SEC-106, DET-008). Each entry's
/// <see cref="AuditEntry.Hmac"/> binds it to its predecessor, so reordering, omission or editing of
/// any row is detected on verification. <b>This is tamper-EVIDENT, not tamper-PROOF</b>: the signing
/// key must come from a platform secret store (DPAPI / OS keychain) in production — see
/// <c>IAuditSigningKeyProvider</c>. A constant key (used in tests / fallback) only detects accidental
/// corruption and unsophisticated modification, never a determined attacker who also holds the key.
/// </summary>
public static class AuditChain
{
    /// <summary>Sentinel prev-hash for the genesis (sequence = 1) entry.</summary>
    public const string GenesisPrevHmac = "0";

    /// <summary>Canonical, stable payload string bound by the HMAC (excludes Sequence, PrevHmac, Hmac).</summary>
    public static string CanonicalPayload(AuditEntry e) =>
        string.Join("|",
            e.OccurredAtUtc.UtcTicks.ToString("D20"),
            ((int)e.Category).ToString(System.Globalization.CultureInfo.InvariantCulture),
            e.Action,
            e.Actor,
            e.SubjectJson,
            e.DecisionTraceJson,
            e.SafeDetailJson,
            e.CreatedAtUtc.UtcTicks.ToString("D20"));

    /// <summary>Computes the HMAC for an entry given its predecessor's HMAC.</summary>
    public static string ComputeHmac(byte[] key, string prevHmac, AuditEntry e)
    {
        var payload = prevHmac + "|" + CanonicalPayload(e);
        var bytes = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>Builds a chained entry following <paramref name="previous"/> (or genesis if null).</summary>
    public static AuditEntry Link(byte[] key, AuditEntry? previous, AuditEntry content)
    {
        var prevHmac = previous?.Hmac ?? GenesisPrevHmac;
        var sequence = (previous?.Sequence ?? 0) + 1;
        var linked = content with { Sequence = sequence, PrevHmac = prevHmac };
        return linked with { Hmac = ComputeHmac(key, prevHmac, linked) };
    }
}
