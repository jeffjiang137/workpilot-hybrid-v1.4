using System.Security.Cryptography;
using System.Text;
using WorkPilot.Contracts.Primitives.Ids;

namespace WorkPilot.Domain.Security;

/// <summary>
/// Deterministic, display-name-free fingerprint for a security event (doc 06 §2):
/// <c>SHA256(type + source_kind + source_id + capability_stable_id? + automation_id? + primary_error_code?)</c>.
/// Two events that share a fingerprint within the aggregation window collapse into one incident.
/// </summary>
public static class SecurityEventFingerprint
{
    public static string Compute(
        SecurityEventType type,
        SourceReference? source,
        string? capabilityStableId,
        AutomationId? automationId,
        string? primaryErrorCode)
    {
        var sb = new StringBuilder();
        sb.Append((int)type).Append('|');
        sb.Append(source?.Kind ?? string.Empty).Append('|');
        sb.Append(source?.Id ?? string.Empty).Append('|');
        sb.Append(capabilityStableId ?? string.Empty).Append('|');
        sb.Append(automationId?.Value ?? string.Empty).Append('|');
        sb.Append(primaryErrorCode ?? string.Empty);

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string Compute(SecurityEvent e) =>
        Compute(e.Type, e.Source, e.CapabilityStableId, e.AutomationId, e.PrimaryErrorCode);
}
