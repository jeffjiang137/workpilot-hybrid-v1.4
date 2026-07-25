using System.Collections.Generic;

namespace WorkPilot.Domain.Automation.Run.Redaction;

/// <summary>
/// A registered secret fingerprint (doc 05 §4 stage 3). The matcher holds only HMAC-SHA256 digests
/// and lengths — never the raw secret value. Implemented by the secret store (Infrastructure/Host);
/// the redactor consumes it without persisting any secret dictionary.
/// </summary>
public sealed record SecretFingerprint(string HmacSha256Base64, int Length);

/// <summary>A span to be redacted within a text (start index + length).</summary>
public readonly record struct RedactionSpan(int Start, int Length);

/// <summary>
/// Detects raw secret values inside a text without holding the raw secret (doc 05 §4 stage 3).
/// Implementations stream-match against registered fingerprints.
/// </summary>
public interface ISecretMatcher
{
    /// <summary>Returns the spans (in character indexes) that match a registered secret.</summary>
    IReadOnlyList<RedactionSpan> Match(string text);
}
