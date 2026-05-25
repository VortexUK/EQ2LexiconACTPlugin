using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace EQ2Lexicon.ACTPlugin.Tests
{
    /// <summary>
    /// Tests for PluginUpdater — the download + SHA-256 verify orchestration
    /// for the self-update flow. We're shipping verified binary code into a
    /// running .NET process; the verification logic is the kind of thing
    /// where a silent regression (e.g. someone replaces ConstantTimeEquals
    /// with == and ships) becomes a real security problem.
    /// </summary>
    public class PluginUpdaterTests
    {
        private class StubFetcher : IDllAssetFetcher
        {
            public byte[]? Response { get; set; }
            public Exception? Throw { get; set; }
            public string? LastUrl { get; private set; }
            public Task<byte[]> FetchAsync(string url)
            {
                LastUrl = url;
                if (Throw != null) throw Throw;
                return Task.FromResult(Response ?? Array.Empty<byte>());
            }
        }

        private static string Sha256Hex(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                var sb = new StringBuilder();
                foreach (var b in sha.ComputeHash(bytes)) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        [Fact]
        public async Task DownloadAndVerifyAsync_HappyPath_ReturnsBytes()
        {
            var bytes = Encoding.UTF8.GetBytes("fake dll content");
            var hex = Sha256Hex(bytes);
            var fetcher = new StubFetcher { Response = bytes };

            var result = await PluginUpdater.DownloadAndVerifyAsync(
                "https://example/plugin.dll", hex, fetcher);

            Assert.True(result.Success);
            Assert.Equal(bytes, result.DllBytes);
            Assert.Equal("https://example/plugin.dll", fetcher.LastUrl);
        }

        [Fact]
        public async Task DownloadAndVerifyAsync_RejectsHashMismatch()
        {
            // What this test is REALLY pinning: a MITM substitution
            // (or a CDN-cached different binary, or a partial download
            // somehow producing the right length) MUST fail the hash
            // check rather than silently install. This is the whole
            // point of the verify step.
            var bytes = Encoding.UTF8.GetBytes("expected content");
            var differentBytes = Encoding.UTF8.GetBytes("MALICIOUS content");
            var expectedHex = Sha256Hex(bytes);
            var fetcher = new StubFetcher { Response = differentBytes };

            var result = await PluginUpdater.DownloadAndVerifyAsync(
                "https://example/plugin.dll", expectedHex, fetcher);

            Assert.False(result.Success);
            Assert.Null(result.DllBytes);
            Assert.Contains("mismatch", result.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task DownloadAndVerifyAsync_RefusesWithoutExpectedHash()
        {
            // GitHub occasionally surfaces a release before the digest
            // is backfilled. Refusing to install without a digest is
            // the right call — we can't tell legitimate bytes from
            // tampered ones with no reference point.
            var fetcher = new StubFetcher { Response = new byte[] { 1, 2, 3 } };
            var result = await PluginUpdater.DownloadAndVerifyAsync(
                "https://example/plugin.dll", "", fetcher);

            Assert.False(result.Success);
            Assert.Contains("digest", result.Message, StringComparison.OrdinalIgnoreCase);
            // Critical: we never even fetched. Don't waste bandwidth.
            Assert.Null(fetcher.LastUrl);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public async Task DownloadAndVerifyAsync_RefusesWithoutUrl(string? url)
        {
            var fetcher = new StubFetcher();
            var result = await PluginUpdater.DownloadAndVerifyAsync(url!, "anyhash", fetcher);
            Assert.False(result.Success);
            Assert.Contains("URL", result.Message);
        }

        [Fact]
        public async Task DownloadAndVerifyAsync_HandlesFetcherException()
        {
            var fetcher = new StubFetcher { Throw = new InvalidOperationException("connection reset") };
            var result = await PluginUpdater.DownloadAndVerifyAsync(
                "https://example/plugin.dll", new string('0', 64), fetcher);

            Assert.False(result.Success);
            Assert.Contains("connection reset", result.Message);
        }

        [Fact]
        public async Task DownloadAndVerifyAsync_RejectsEmptyDownload()
        {
            var fetcher = new StubFetcher { Response = Array.Empty<byte>() };
            var result = await PluginUpdater.DownloadAndVerifyAsync(
                "https://example/plugin.dll", new string('0', 64), fetcher);
            Assert.False(result.Success);
            Assert.Contains("no bytes", result.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task DownloadAndVerifyAsync_RejectsOversizedDownload()
        {
            // Belt-and-brace cap. Real DLL is ~76 KB; if a download
            // exceeds 10 MB something is wrong (wrong file from CDN,
            // hostile substitution, etc.). Refusing here means the
            // user sees a clear error rather than the plugin trying
            // to verify a multi-hundred-MB blob.
            var oversize = new byte[PluginUpdater.MaxDllBytes + 1];
            var fetcher = new StubFetcher { Response = oversize };
            var result = await PluginUpdater.DownloadAndVerifyAsync(
                "https://example/plugin.dll", new string('0', 64), fetcher);
            Assert.False(result.Success);
            Assert.Contains("too large", result.Message, StringComparison.OrdinalIgnoreCase);
        }

        // ── ValidateAssetUri (H2 hardening — v0.1.13) ─────────────────────
        // The audit (v0.1.5 → v0.1.12) flagged that a tampered GitHub JSON
        // could substitute the asset URL to anywhere on the internet. The
        // SHA-256 verify is the actual gate against bytes-substitution,
        // but pinning the URL hardens against information-disclosure (the
        // plugin's User-Agent gets sent wherever the JSON points) and
        // against future bugs where the verify could be bypassed.

        [Theory]
        [InlineData("https://github.com/VortexUK/EQ2LexiconACTPlugin/releases/download/v0.1.12/EQ2Lexicon.ACTPlugin.dll")]
        [InlineData("https://objects.githubusercontent.com/anything")]
        [InlineData("https://release-assets.githubusercontent.com/whatever")]
        public void ValidateAssetUri_AcceptsGitHubHostsOverHttps(string url)
        {
            HttpDllAssetFetcher.ValidateAssetUri(new Uri(url));
        }

        [Theory]
        [InlineData("http://github.com/foo")]          // scheme downgrade
        [InlineData("ftp://github.com/foo")]            // non-http(s)
        [InlineData("https://evil.com/foo")]            // off-domain
        [InlineData("https://github.com.evil.com/foo")] // domain-suffix smuggling
        [InlineData("https://notgithub.com/foo")]
        public void ValidateAssetUri_RejectsHostileOrDowngradedUrls(string url)
        {
            Assert.Throws<InvalidOperationException>(() =>
                HttpDllAssetFetcher.ValidateAssetUri(new Uri(url)));
        }

        [Fact]
        public void IsAllowedAssetHost_HandlesEdgeCases()
        {
            // Bare apex match
            Assert.True(HttpDllAssetFetcher.IsAllowedAssetHost("github.com"));
            Assert.True(HttpDllAssetFetcher.IsAllowedAssetHost("GITHUB.COM"));
            // Subdomains of githubusercontent.com — GH uses several
            // for releases (objects., release-assets., etc.)
            Assert.True(HttpDllAssetFetcher.IsAllowedAssetHost("objects.githubusercontent.com"));
            Assert.True(HttpDllAssetFetcher.IsAllowedAssetHost("anything.githubusercontent.com"));
            // Subdomain attack — must not match github.com as a suffix
            Assert.False(HttpDllAssetFetcher.IsAllowedAssetHost("evil-github.com"));
            Assert.False(HttpDllAssetFetcher.IsAllowedAssetHost("github.com.evil.com"));
            // Empty / null
            Assert.False(HttpDllAssetFetcher.IsAllowedAssetHost(""));
            Assert.False(HttpDllAssetFetcher.IsAllowedAssetHost(null!));
        }

        [Fact]
        public async Task DownloadAndVerifyAsync_HashCompareIsCaseInsensitive()
        {
            // GH emits lowercase; we accept either case in the
            // expected-hash argument so future shape changes don't
            // break us. The actual hash we compute is always lowercase.
            var bytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
            var hexLower = Sha256Hex(bytes);
            var hexUpper = hexLower.ToUpperInvariant();

            var fetcher = new StubFetcher { Response = bytes };
            var result = await PluginUpdater.DownloadAndVerifyAsync(
                "https://example/plugin.dll", hexUpper, fetcher);

            Assert.True(result.Success);
        }
    }
}
