using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace WorkPilot.Domain.Automation.Run.Redaction;

/// <summary>
/// Streaming HMAC fingerprint matcher (doc 05 §4 stage 3). Given the transient lease HMAC key and a
/// set of registered <see cref="SecretFingerprint"/>s (HMAC digest + raw UTF-8 byte length), it slides a
/// window of each registered length over the text, recomputes HMAC-SHA256, and reports matching spans.
/// This detects a registered secret even when it is split across JSON chunk boundaries or encoded as
/// Unicode: the window operates on the exact byte sequence used when the fingerprint was registered, and
/// byte offsets are mapped back to character indexes. Holds no raw secret.
/// </summary>
public sealed class KnownSecretMatcher : ISecretMatcher
{
    private readonly byte[] _key;
    private readonly IReadOnlyList<SecretFingerprint> _fingerprints;

    public KnownSecretMatcher(byte[] leaseHmacKey, IReadOnlyList<SecretFingerprint> fingerprints)
    {
        _key = leaseHmacKey ?? Array.Empty<byte>();
        _fingerprints = fingerprints ?? Array.Empty<SecretFingerprint>();
    }

    public IReadOnlyList<RedactionSpan> Match(string text)
    {
        var found = new List<RedactionSpan>();
        if (string.IsNullOrEmpty(text) || _fingerprints.Count == 0)
            return found;

        var bytes = Encoding.UTF8.GetBytes(text);
        var charStartByte = new int[text.Length + 1];
        var bytePos = 0;
        for (var ci = 0; ci < text.Length; ci++)
        {
            charStartByte[ci] = bytePos;
            bytePos += Encoding.UTF8.GetByteCount(text[ci].ToString());
        }
        charStartByte[text.Length] = bytes.Length;

        foreach (var fp in _fingerprints)
        {
            if (fp.Length <= 0 || fp.Length > bytes.Length)
                continue;
            if (!TryDecode(fp.HmacSha256Base64, out var expected))
                continue;

            using var hmac = new HMACSHA256(_key);
            for (var bStart = 0; bStart + fp.Length <= bytes.Length; bStart++)
            {
                var window = new Span<byte>(bytes, bStart, fp.Length);
                var digest = hmac.ComputeHash(window.ToArray());
                if (!digest.AsSpan().SequenceEqual(expected))
                    continue;

                var cStart = ByteToChar(charStartByte, bStart);
                var cEnd = ByteToChar(charStartByte, bStart + fp.Length);
                if (cStart >= 0 && cEnd >= cStart)
                    found.Add(new RedactionSpan(cStart, cEnd - cStart));
            }
        }
        return found;
    }

    private static int ByteToChar(int[] charStartByte, int byteOffset)
    {
        // charStartByte is monotonically increasing; binary search for the largest index with value <= byteOffset
        var lo = 0;
        var hi = charStartByte.Length - 1;
        var result = 0;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            if (charStartByte[mid] <= byteOffset) { result = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return result;
    }

    private static bool TryDecode(string base64, out byte[] bytes)
    {
        try { bytes = Convert.FromBase64String(base64); return true; }
        catch { bytes = Array.Empty<byte>(); return false; }
    }
}
