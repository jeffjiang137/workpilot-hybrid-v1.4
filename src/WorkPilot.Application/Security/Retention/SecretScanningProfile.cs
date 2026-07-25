using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using WorkPilot.Domain.Automation.Run.Redaction;

namespace WorkPilot.Application.Security.Retention;

/// <summary>
/// Supplies the secret-scanning configuration used by the support-bundle redaction scan
/// (doc 05 §4 stage 3 + §10.2, LOG-A05 / SEC-A14). Holds a stable scanning key and a fixed set of
/// canary tokens. Registered secrets are converted on demand into HMAC-SHA256 fingerprints (the raw
/// value is never retained) and exposed via <see cref="BuildMatcher"/>, which feeds the redaction
/// pipeline's Stage-3 known-secret matcher. Canary tokens feed Stage 7 only and must never appear in
/// the matcher: a canary that matched as a "known secret" would be silently redacted instead of
/// triggering the hard fail that proves redaction actually ran (and ran first).
/// </summary>
public sealed class SecretScanningProfile
{
    private readonly byte[] _scanningKey;
    private readonly ISet<string> _canaryTokens;

    public SecretScanningProfile(byte[] scanningKey, IEnumerable<string> canaryTokens)
    {
        _scanningKey = scanningKey ?? throw new ArgumentNullException(nameof(scanningKey));
        _canaryTokens = new HashSet<string>(canaryTokens ?? Array.Empty<string>(), StringComparer.Ordinal);
    }

    /// <summary>Stable canary tokens. A canary that survives redaction fails the support-bundle build (releaseMode).</summary>
    public ISet<string> CanaryTokens => _canaryTokens;

    /// <summary>
    /// Builds a known-secret matcher from raw secret values. Each secret is reduced to an
    /// HMAC-SHA256 fingerprint (digest + UTF-8 byte length); the raw value's bytes are zeroed
    /// immediately and never stored. The returned matcher holds only fingerprints.
    /// </summary>
    public ISecretMatcher BuildMatcher(IEnumerable<string> knownSecrets)
    {
        var fingerprints = new List<SecretFingerprint>();
        if (knownSecrets is not null)
        {
            foreach (var secret in knownSecrets)
            {
                if (string.IsNullOrEmpty(secret)) continue;
                var bytes = Encoding.UTF8.GetBytes(secret);
                try
                {
                    using var hmac = new HMACSHA256(_scanningKey);
                    var digest = hmac.ComputeHash(bytes);
                    fingerprints.Add(new SecretFingerprint(Convert.ToBase64String(digest), bytes.Length));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }
        }
        return new KnownSecretMatcher(_scanningKey, fingerprints);
    }
}
