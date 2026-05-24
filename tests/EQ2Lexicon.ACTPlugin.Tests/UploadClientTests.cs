using Xunit;

namespace EQ2Lexicon.ACTPlugin.Tests
{
    /// <summary>
    /// Tests for the small JSON / string helpers inside UploadClient. The
    /// HTTP send path itself isn't tested here — that needs an in-process
    /// listener which the user explicitly opted out of.
    /// </summary>
    public class UploadClientTests
    {
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
