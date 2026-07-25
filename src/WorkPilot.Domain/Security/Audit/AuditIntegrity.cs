namespace WorkPilot.Domain.Security.Audit;

/// <summary>Outcome of an audit-log integrity verification pass (DET-008 trigger source).</summary>
public sealed record AuditIntegrityReport(
    bool Intact,
    int VerifiedCount,
    AuditEntry? FirstBroken,
    string? Error)
{
    public static AuditIntegrityReport Ok(int verified) =>
        new(true, verified, null, null);

    public static AuditIntegrityReport Broken(AuditEntry broken, string error) =>
        new(false, 0, broken, error);
}

/// <summary>
/// Pure re-player of the HMAC chain. Verifies sequence continuity, ordering and per-entry HMAC so
/// that editing, deleting, inserting or reordering any row is caught (SEC-106, DET-008).
/// </summary>
public static class AuditIntegrity
{
    public static AuditIntegrityReport Verify(IReadOnlyList<AuditEntry> entries, byte[] key)
    {
        if (entries.Count == 0)
            return AuditIntegrityReport.Ok(0);

        // Replay in sequence order; gaps or reordering break continuity.
        var ordered = entries.OrderBy(e => e.Sequence, Comparer<long>.Default).ToArray();
        var expectedPrev = AuditChain.GenesisPrevHmac;
        var verified = 0;

        for (var i = 0; i < ordered.Length; i++)
        {
            var e = ordered[i];

            if (i > 0 && ordered[i].Sequence != ordered[i - 1].Sequence + 1)
                return AuditIntegrityReport.Broken(e,
                    $"审计序列不连续：期望 {ordered[i - 1].Sequence + 1}，实际 {e.Sequence}（可能缺失或重排）");

            if (!string.Equals(e.PrevHmac, expectedPrev, StringComparison.Ordinal))
                return AuditIntegrityReport.Broken(e,
                    $"审计链断裂：PrevHmac 不匹配（行 {e.Sequence}）");

            var expectedHmac = AuditChain.ComputeHmac(key, expectedPrev, e);
            if (!string.Equals(e.Hmac, expectedHmac, StringComparison.Ordinal))
                return AuditIntegrityReport.Broken(e,
                    $"审计 HMAC 校验失败（行 {e.Sequence}）：内容可能被篡改");

            expectedPrev = e.Hmac;
            verified++;
        }

        return AuditIntegrityReport.Ok(verified);
    }
}
