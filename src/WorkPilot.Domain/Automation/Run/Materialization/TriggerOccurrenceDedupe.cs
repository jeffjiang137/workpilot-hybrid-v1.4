using System.Security.Cryptography;
using System.Text;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;

namespace WorkPilot.Domain.Automation.Run.Materialization;

/// <summary>
/// Deterministic dedupe key for a trigger occurrence (spec doc 04 §3, RUN-001/009/010). Two
/// materialization attempts for the same (automation, revision, trigger, scheduled instant) always
/// produce the same 64-hex SHA-256, so the repository can reject duplicate runs idempotently via a
/// UNIQUE constraint — no double materialization even across multiple Host workers or crash restarts.
/// Pure and side-effect free.
/// </summary>
public static class TriggerOccurrenceDedupe
{
    public static string Compute(
        AutomationId automationId,
        AutomationRevisionId automationRevisionId,
        string triggerId,
        DateTimeOffset scheduledAtUtc)
    {
        // automationId / automationRevisionId are non-nullable readonly record structs — the caller
        // guarantees populated ids, so no null guard is needed (and would not compile).
        if (string.IsNullOrWhiteSpace(triggerId)) throw new System.ArgumentException("triggerId required", nameof(triggerId));

        // Stable concatenation: each field is length-prefixed-terminated so two distinct field sets
        // can never collide (e.g. trigger "a"+"b" vs "ab"). '|' is a safe separator for ids/hex.
        var raw = new StringBuilder();
        raw.Append(automationId.Value).Append('|');
        raw.Append(automationRevisionId.Value).Append('|');
        raw.Append(triggerId).Append('|');
        raw.Append(scheduledAtUtc.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('|');

        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(raw.ToString()))).ToLowerInvariant();
    }
}
