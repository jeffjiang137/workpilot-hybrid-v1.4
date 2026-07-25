using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using WorkPilot.Domain.Automation.Run.Redaction;
using Xunit;

namespace WorkPilot.Domain.Tests.Automation.Run.Redaction;

public class RedactionPipelineTests
{
    private static SecretFingerprint Fingerprint(byte[] key, string secret)
    {
        using var h = new HMACSHA256(key);
        var digest = h.ComputeHash(Encoding.UTF8.GetBytes(secret));
        return new SecretFingerprint(Convert.ToBase64String(digest), Encoding.UTF8.GetBytes(secret).Length);
    }

    [Fact]
    public void Header_and_cookie_values_are_removed_LOG_A04()
    {
        var input = "Authorization: Bearer abc123; Cookie: session=xyz; normal=ok";
        var r = RedactionPipeline.RedactSerialized(input, null, null, false);
        Assert.Contains("[REDACTED:header]", r.Value);
        Assert.DoesNotContain("abc123", r.Value);
        Assert.DoesNotContain("session=xyz", r.Value);
        Assert.Contains("normal=ok", r.Value);
    }

    [Fact]
    public void Url_query_and_fragment_are_stripped_LOG_A04()
    {
        var input = "see https://example.com/path/to?token=secret&x=1#frag for details";
        var r = RedactionPipeline.RedactSerialized(input, null, null, false);
        Assert.DoesNotContain("token=secret", r.Value);
        Assert.DoesNotContain("?token", r.Value);
        Assert.Contains("[", r.Value); // host aliased
    }

    [Fact]
    public void Jwt_pem_bearer_basic_and_github_token_redacted_LOG_A04()
    {
        var input = "jwt=eyJhbGci.eyJzdWIi.SflKxw; pem=-----BEGIN RSA PRIVATE KEY-----\nMIIabc\n-----END RSA PRIVATE KEY-----; bearer=Bearer xyz789; basic=Basic dXNlcjpw; gh=ghp_ABCDEFGHIJ1234567890";
        var r = RedactionPipeline.RedactSerialized(input, null, null, false);
        Assert.Contains("[REDACTED:jwt]", r.Value);
        Assert.Contains("[REDACTED:pem]", r.Value);
        Assert.Contains("[REDACTED:bearer]", r.Value);
        Assert.Contains("[REDACTED:basic]", r.Value);
        Assert.Contains("[REDACTED:ghtoken]", r.Value);
        Assert.DoesNotContain("eyJhbGci", r.Value);
        Assert.DoesNotContain("MIIabc", r.Value);
        Assert.DoesNotContain("xyz789", r.Value);
        Assert.DoesNotContain("dXNlcjpw", r.Value);
        Assert.DoesNotContain("ghp_ABCDEFGHIJ1234567890", r.Value);
    }

    [Fact]
    public void Bearer_keyword_is_case_insensitive_LOG_A03()
    {
        var input = "auth: bEaReR s3cr3t";
        var r = RedactionPipeline.RedactSerialized(input, null, null, false);
        Assert.Contains("[REDACTED:bearer]", r.Value);
        Assert.DoesNotContain("s3cr3t", r.Value);
    }

    [Fact]
    public void Oversize_string_is_truncated_LOG_A06_limit()
    {
        var input = new string('x', 5000);
        var r = RedactionPipeline.RedactSerialized(input, null, null, false);
        Assert.True(r.Truncated);
        Assert.True(r.Value.Length <= 2000);
    }

    [Fact]
    public void Registered_canary_is_redacted_zero_plaintext_LOG_A05()
    {
        var key = Encoding.UTF8.GetBytes("lease-key-1234567890");
        var canary = "CANARY-9d2f-plaintoken";
        var matcher = new KnownSecretMatcher(key, new[] { Fingerprint(key, canary) });
        var input = "{\"note\":\"value contains CANARY-9d2f-plaintoken inside\"}";

        var r = RedactionPipeline.RedactSerialized(input, matcher, null, false);

        Assert.DoesNotContain(canary, r.Value);
        Assert.Contains("[REDACTED:secret]", r.Value);
        Assert.DoesNotContain("RUN_REDACTION_CANARY", r.Value); // survived? no -> no violation
    }

    [Fact]
    public void Unredacted_canary_survives_triggers_violation_hook()
    {
        // canary passed as a token but NOT registered with the matcher -> should be caught by the hook
        var canary = "CANARY-leak-me";
        var input = "plain text CANARY-leak-me here";
        var r = RedactionPipeline.RedactSerialized(input, null, new HashSet<string> { canary }, false);
        Assert.Contains("RUN_REDACTION_CANARY", r.ViolationCodes); // violation recorded
        Assert.Contains(canary, r.Value); // still present (test harness would fail)
    }

    [Fact]
    public void Release_mode_replaces_body_on_canary_survival()
    {
        var canary = "CANARY-release";
        var input = "x CANARY-release y";
        var r = RedactionPipeline.RedactSerialized(input, null, new HashSet<string> { canary }, true);
        Assert.Equal("[REDACTION_FAILURE]", r.Value);
        Assert.Contains("RUN_REDACTION_FAILURE", r.ViolationCodes);
    }
}
