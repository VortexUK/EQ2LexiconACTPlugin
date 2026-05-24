using Xunit;

namespace EQ2Lexicon.ACTPlugin.Tests
{
    /// <summary>
    /// Tests for the small JSON / string helpers inside UploadClient and
    /// the URL-validation guard. The HTTP send path itself isn't tested
    /// here — that needs an in-process listener which the user explicitly
    /// opted out of.
    /// </summary>
    public class UploadClientTests
    {
        // ── ValidateServerUrl ──────────────────────────────────────────────
        // Catches the two failure modes that would otherwise leak the bearer
        // token to the wire: plain HTTP (visible on untrusted Wi-Fi) and
        // non-http schemes (file://, javascript:, etc.).

        [Theory]
        [InlineData("https://eq2lexicon.up.railway.app")]
        [InlineData("https://example.test")]
        [InlineData("https://eq2lexicon.up.railway.app/")]
        [InlineData("https://eq2lexicon.up.railway.app/api")]
        public void ValidateServerUrl_AcceptsHttps(string url)
        {
            Assert.Null(UploadClient.ValidateServerUrl(url));
        }

        [Theory]
        [InlineData("http://localhost")]
        [InlineData("http://localhost:8000")]
        [InlineData("http://127.0.0.1:8000")]
        [InlineData("http://127.0.0.1/api")]
        [InlineData("http://[::1]:8000")]
        public void ValidateServerUrl_AcceptsHttpLoopback(string url)
        {
            // Self-hosted dev backends commonly run on http://localhost — we
            // can safely allow plain HTTP for the loopback range because
            // traffic never leaves the machine.
            Assert.Null(UploadClient.ValidateServerUrl(url));
        }

        [Theory]
        [InlineData("http://eq2lexicon.up.railway.app")]
        [InlineData("http://example.com")]
        [InlineData("http://10.0.0.5:8000")]
        public void ValidateServerUrl_RejectsPlainHttpOnRemote(string url)
        {
            // The whole reason the audit flagged this: a typo'd `http://`
            // would POST the bearer token in plaintext over the wire.
            var err = UploadClient.ValidateServerUrl(url);
            Assert.NotNull(err);
            Assert.Contains("https://", err);
        }

        [Theory]
        [InlineData("file:///c:/passwd")]
        [InlineData("javascript:alert(1)")]
        [InlineData("ftp://example.com")]
        [InlineData("ws://example.com")]
        public void ValidateServerUrl_RejectsNonHttpSchemes(string url)
        {
            Assert.NotNull(UploadClient.ValidateServerUrl(url));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void ValidateServerUrl_RejectsEmpty(string? url)
        {
            var err = UploadClient.ValidateServerUrl(url);
            Assert.NotNull(err);
            Assert.Contains("empty", err);
        }

        [Theory]
        [InlineData("not a url")]
        [InlineData("//evil.com")]
        [InlineData("just/a/path")]
        public void ValidateServerUrl_RejectsMalformed(string url)
        {
            Assert.NotNull(UploadClient.ValidateServerUrl(url));
        }


        // ── ExtractJsonString ──────────────────────────────────────────────
        // Minimal JSON string-field extractor used to pluck "detail" out of
        // FastAPI error bodies and "status" out of success bodies. NOT a
        // general-purpose JSON parser; tests pin the shapes we actually
        // encounter on the server.

        [Fact]
        public void ExtractJsonString_PullsSimpleField()
        {
            var body = "{\"status\": \"inserted\", \"id\": 42}";
            Assert.Equal("inserted", UploadClient.ExtractJsonString(body, "status"));
        }

        [Fact]
        public void ExtractJsonString_HandlesNoSpaceAfterColon()
        {
            var body = "{\"status\":\"inserted\"}";
            Assert.Equal("inserted", UploadClient.ExtractJsonString(body, "status"));
        }

        [Fact]
        public void ExtractJsonString_PullsFastApiDetail()
        {
            // FastAPI 4xx bodies look like {"detail": "..."} — the upload
            // path surfaces this in error messages.
            var body = "{\"detail\":\"Token revoked\"}";
            Assert.Equal("Token revoked", UploadClient.ExtractJsonString(body, "detail"));
        }

        [Fact]
        public void ExtractJsonString_HandlesEscapedQuote()
        {
            // The extractor unescapes a single \" inside a string value.
            // Worth pinning since it's our one bit of JSON cleverness.
            var body = "{\"detail\":\"He said \\\"hi\\\" loudly\"}";
            var got = UploadClient.ExtractJsonString(body, "detail");
            Assert.Equal("He said \"hi\" loudly", got);
        }

        [Fact]
        public void ExtractJsonString_MissingFieldReturnsNull()
        {
            var body = "{\"status\":\"inserted\"}";
            Assert.Null(UploadClient.ExtractJsonString(body, "detail"));
        }

        [Fact]
        public void ExtractJsonString_EmptyInputReturnsNull()
        {
            Assert.Null(UploadClient.ExtractJsonString("", "status"));
            Assert.Null(UploadClient.ExtractJsonString(null!, "status"));
        }

        [Fact]
        public void ExtractJsonString_NonStringValueReturnsNull()
        {
            // {"status": 42} — not a string. Our extractor refuses rather
            // than coercing.
            var body = "{\"status\": 42}";
            Assert.Null(UploadClient.ExtractJsonString(body, "status"));
        }

        // ── Truncate ───────────────────────────────────────────────────────

        [Fact]
        public void Truncate_ShortInputUnchanged()
        {
            Assert.Equal("hello", UploadClient.Truncate("hello", 100));
        }

        [Fact]
        public void Truncate_LongInputAddsEllipsis()
        {
            var got = UploadClient.Truncate(new string('x', 600), 500);
            Assert.Equal(500 + 1, got.Length);   // 500 chars + the ellipsis char
            Assert.EndsWith("…", got);
        }

        [Fact]
        public void Truncate_EmptyStays()
        {
            Assert.Equal("", UploadClient.Truncate("", 100));
            Assert.Equal("", UploadClient.Truncate(null!, 100));
        }

        [Fact]
        public void Truncate_ExactBoundaryNoEllipsis()
        {
            var s = new string('x', 100);
            Assert.Equal(s, UploadClient.Truncate(s, 100));
        }
    }
}
