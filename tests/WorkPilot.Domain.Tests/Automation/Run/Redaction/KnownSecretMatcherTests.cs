using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using WorkPilot.Domain.Automation.Run.Redaction;
using Xunit;

namespace WorkPilot.Domain.Tests.Automation.Run.Redaction;

public class KnownSecretMatcherTests
{
    private static byte[] Key => Encoding.UTF8.GetBytes("transient-lease-key-0123456789");
    private static SecretFingerprint Fp(byte[] key, string secret)
    {
        using var h = new HMACSHA256(key);
        var digest = h.ComputeHash(Encoding.UTF8.GetBytes(secret));
        return new SecretFingerprint(Convert.ToBase64String(digest), Encoding.UTF8.GetBytes(secret).Length);
    }

    [Fact]
    public void Detects_exact_secret_in_text()
    {
        var key = Key;
        var secret = "SuperSecretToken";
        var matcher = new KnownSecretMatcher(key, new[] { Fp(key, secret) });
        var spans = matcher.Match("prefix SuperSecretToken suffix");
        Assert.Single(spans);
        Assert.Equal(7, spans[0].Start);
        Assert.Equal(secret.Length, spans[0].Length);
    }

    [Fact]
    public void Detects_secret_adjacent_to_json_punctuation()
    {
        var key = Key;
        var secret = "SuperSecretToken";
        var matcher = new KnownSecretMatcher(key, new[] { Fp(key, secret) });
        var text = "{\"k\":\"SuperSecretToken\"}"; // quotes hug the secret
        var spans = matcher.Match(text);
        Assert.Single(spans);
        Assert.Equal(secret, text.Substring(spans[0].Start, spans[0].Length));
    }

    [Fact]
    public void Detects_unicode_secret_LOG_A03()
    {
        var key = Key;
        var secret = "pässwörd-ψ";
        var matcher = new KnownSecretMatcher(key, new[] { Fp(key, secret) });
        var text = "x pässwörd-ψ y";
        var spans = matcher.Match(text);
        Assert.Single(spans);
        Assert.Equal(secret, text.Substring(spans[0].Start, spans[0].Length));
    }

    [Fact]
    public void Detects_secret_split_across_stream_chunk_boundary()
    {
        // The secret is contiguous in the text but the "chunk boundary" (a read boundary) falls inside it.
        var key = Key;
        var secret = "LongSecretValue";
        var matcher = new KnownSecretMatcher(key, new[] { Fp(key, secret) });
        var text = "abcdefghijklmnop" + secret + "qrstuvwxyz";
        var spans = matcher.Match(text);
        Assert.Single(spans);
        Assert.Equal(secret, text.Substring(spans[0].Start, spans[0].Length));
    }

    [Fact]
    public void Different_case_is_not_matched_case_exact()
    {
        var key = Key;
        var secret = "TokenABC";
        var matcher = new KnownSecretMatcher(key, new[] { Fp(key, secret) });
        var spans = matcher.Match("tokenabc"); // lowercase -> HMAC mismatch
        Assert.Empty(spans);
    }

    [Fact]
    public void Unknown_text_has_no_match()
    {
        var key = Key;
        var matcher = new KnownSecretMatcher(key, new[] { Fp(key, "right") });
        Assert.Empty(matcher.Match("completely unrelated content"));
    }
}
