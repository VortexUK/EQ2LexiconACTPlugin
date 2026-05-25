using System;
using Xunit;

namespace EQ2Lexicon.ACTPlugin.Tests
{
    /// <summary>
    /// Tests for PayloadSigner — HMAC-SHA256 over the upload body keyed by
    /// the user's API token.
    ///
    /// Includes one RFC-4231 test vector to confirm we're computing the
    /// real algorithm (and not, say, plain SHA-256 of the concatenation).
    /// The server-side validator in the EQ2Lexicon repo MUST agree with
    /// these tests; if either side changes, both have to be reconciled.
    /// </summary>
    public class PayloadSignerTests
    {
        [Fact]
        public void Sign_ProducesDeterministicHex()
        {
            var a = PayloadSigner.Sign("hello world", "my-token");
            var b = PayloadSigner.Sign("hello world", "my-token");
            Assert.Equal(a, b);
            // 64 hex chars = 32-byte SHA-256 digest, lowercase.
            Assert.Equal(64, a.Length);
            Assert.Matches("^[0-9a-f]+$", a);
        }

        [Fact]
        public void Sign_DiffersWhenKeyChanges()
        {
            // Same body, different key. If these matched, the key was being
            // ignored (HMAC implementation bug).
            var a = PayloadSigner.Sign("body", "token-A");
            var b = PayloadSigner.Sign("body", "token-B");
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void Sign_DiffersWhenBodyChanges()
        {
            // Same key, different body. The whole point of HMAC — if these
            // matched, tampering with the body wouldn't be detectable.
            var a = PayloadSigner.Sign("{\"dps\": 5000}", "token");
            var b = PayloadSigner.Sign("{\"dps\": 50000}", "token");
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void Sign_MatchesRfc4231TestVector()
        {
            // RFC 4231 § 4.3 — HMAC-SHA-256 Test Case 2. If this fails,
            // we're not computing the real algorithm. Picked TC2 over TC1
            // because TC1's key is 0x0b*20 (vertical tab, whitespace-class),
            // which Sign() correctly rejects via the IsNullOrWhiteSpace
            // guard — a good catch for production but bad for a test vector.
            //   Key = "Jefe"
            //   Data = "what do ya want for nothing?"
            //   Expected = 5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843
            var sig = PayloadSigner.Sign("what do ya want for nothing?", "Jefe");
            Assert.Equal("5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843", sig);
        }

        [Fact]
        public void Sign_HandlesEmptyBody()
        {
            // Edge case — an empty body still has a valid HMAC. Server
            // would reject for being empty before reaching here, but the
            // signer itself must not throw.
            var sig = PayloadSigner.Sign("", "any-token");
            Assert.Equal(64, sig.Length);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void Sign_ThrowsOnMissingToken(string? token)
        {
            // Signing with an empty key would produce a misleadingly-
            // authoritative-looking signature ("HMAC over empty key" still
            // returns 32 bytes). Caller MUST validate the token first; we
            // fail loudly to catch bugs in the caller.
            Assert.Throws<ArgumentException>(() => PayloadSigner.Sign("body", token!));
        }

        [Fact]
        public void Sign_IsCaseConsistentInOutput()
        {
            // We document the output as lower-case hex. The server's
            // constant-time compare won't normalise; pinning this in a test
            // prevents an accidental .ToUpper() from breaking validation.
            var sig = PayloadSigner.Sign("body", "token");
            Assert.Equal(sig.ToLowerInvariant(), sig);
        }

        [Fact]
        public void SignatureHeaderName_IsTheStringWeShipped()
        {
            // Pinned because changing this header name without coordinating
            // a server-side change would silently break HMAC validation
            // (server falls back to "no signature header" and accepts
            // unsigned uploads — the worst kind of regression).
            Assert.Equal("X-Lexicon-Signature", PayloadSigner.SignatureHeaderName);
        }
    }
}
