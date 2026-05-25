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

        // ── JSON escape sequences (RFC 8259 § 7) ───────────────────────────
        // FastAPI's JSONResponse emits raw UTF-8 (ensure_ascii=False) so
        // \uXXXX escapes don't show up in practice today, but any other
        // JSON producer (or a config change) would emit them — and the
        // pre-v0.1.9 extractor turned "你" into the literal text
        // "u4f60", which is how the non-ASCII Discord name rendered as
        // boxes in the test-connection label. Pin every escape RFC 8259
        // defines so a regression here can't slip through silently.

        [Fact]
        public void ExtractJsonString_HandlesAllStandardEscapes()
        {
            // \\ \/ \b \f \n \r \t — order matches the RFC table.
            var body = "{\"detail\":\"a\\\\b\\/c\\bd\\fe\\nf\\rg\\th\"}";
            Assert.Equal("a\\b/c\bd\fe\nf\rg\th", UploadClient.ExtractJsonString(body, "detail"));
        }

        [Fact]
        public void ExtractJsonString_HandlesUnicodeEscapes()
        {
            // 你好 = 你好 (CJK "hello"). Pre-fix this came out as
            // the literal text "u4f60u597d" and rendered as boxes in the
            // label. Mojibake-shaped bug we'd have shipped to every
            // non-ASCII Discord username if we hadn't caught it.
            var body = "{\"discord_name\":\"\\u4f60\\u597d\"}";
            Assert.Equal("你好", UploadClient.ExtractJsonString(body, "discord_name"));
        }

        [Fact]
        public void ExtractJsonString_HandlesEmojiSurrogatePair()
        {
            // Emoji outside the BMP are emitted as a UTF-16 surrogate
            // pair of \uXXXX escapes. 😀 (U+1F600) = D83D + DE00.
            // System.String stores the two chars as a valid surrogate
            // pair so concatenation just works.
            var body = "{\"discord_name\":\"\\uD83D\\uDE00\"}";
            Assert.Equal("😀", UploadClient.ExtractJsonString(body, "discord_name"));
        }

        [Fact]
        public void ExtractJsonString_HandlesMixedAsciiAndEscapes()
        {
            // The real-world shape: a name that's mostly ASCII but
            // contains a single accented char that the server escaped.
            var body = "{\"discord_name\":\"caf\\u00e9\"}";
            Assert.Equal("café", UploadClient.ExtractJsonString(body, "discord_name"));
        }

        [Fact]
        public void ExtractJsonString_MalformedUnicodeEscapeReturnsNull()
        {
            // \u followed by anything that isn't 4 hex digits is a
            // protocol error — bail rather than guess.
            var body = "{\"detail\":\"foo \\u4f6 bar\"}";  // only 3 hex digits
            Assert.Null(UploadClient.ExtractJsonString(body, "detail"));
        }

        [Fact]
        public void ExtractJsonString_RejectsLoneHighSurrogate()
        {
            // A high surrogate (\uD800-\uDBFF) is only valid when
            // immediately followed by a low surrogate. A hostile server
            // sending the high alone would otherwise produce an
            // ill-formed UTF-16 string that downstream code
            // (clipboard, JSON re-serialise) might choke on.
            var body = "{\"detail\":\"\\uD83Dhello\"}";
            Assert.Null(UploadClient.ExtractJsonString(body, "detail"));
        }

        [Fact]
        public void ExtractJsonString_RejectsLoneLowSurrogate()
        {
            // Low surrogates (\uDC00-\uDFFF) MUST be preceded by a
            // high surrogate. One by itself is a protocol error.
            var body = "{\"detail\":\"\\uDE00\"}";
            Assert.Null(UploadClient.ExtractJsonString(body, "detail"));
        }

        [Fact]
        public void ExtractJsonString_RejectsHighSurrogateAtEndOfString()
        {
            // Truncated emoji at the end of the string — high
            // surrogate with no following escape sequence at all.
            var body = "{\"detail\":\"\\uD83D\"}";
            Assert.Null(UploadClient.ExtractJsonString(body, "detail"));
        }

        [Fact]
        public void ExtractJsonString_RejectsHighSurrogateFollowedByNonLow()
        {
            // \uD83DA — high followed by a regular ASCII 'A'
            // (not a low surrogate). Ill-formed; reject.
            var body = "{\"detail\":\"\\uD83D\\u0041\"}";
            Assert.Null(UploadClient.ExtractJsonString(body, "detail"));
        }

        [Fact]
        public void ExtractJsonString_HandlesRawUtf8FromFastApi()
        {
            // The default FastAPI path: bytes-on-the-wire are raw UTF-8,
            // ReadBodyUtf8Async produces a string with the actual
            // characters (not escapes). Extractor passes them through
            // unchanged — this is the happy path that the field today.
            var body = "{\"discord_name\":\"你好\"}";
            Assert.Equal("你好", UploadClient.ExtractJsonString(body, "discord_name"));
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
