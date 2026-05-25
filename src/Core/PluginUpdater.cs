using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace EQ2Lexicon.ACTPlugin
{
    /// <summary>
    /// Outcome of a download + verify attempt. Plain DTO so Plugin and
    /// the UI can pattern-match without a sea of out-parameters.
    /// </summary>
    public class DownloadResult
    {
        /// <summary>True iff bytes were fetched AND hash matched.</summary>
        public bool Success { get; set; }

        /// <summary>The verified DLL bytes when <see cref="Success"/> is true; null otherwise.</summary>
        public byte[]? DllBytes { get; set; }

        /// <summary>Short user-facing message — what to show in the SettingsPanel.</summary>
        public string Message { get; set; } = "";
    }

    /// <summary>
    /// Abstracts the binary fetch so unit tests can supply canned bytes
    /// without going to the network. Symmetrical with
    /// <see cref="IReleaseFeedFetcher"/>.
    /// </summary>
    public interface IDllAssetFetcher
    {
        Task<byte[]> FetchAsync(string url);
    }

    /// <summary>
    /// Production fetcher — wraps HttpClient and applies hardening
    /// suitable for downloading executable code from the internet:
    ///
    ///   * Initial URL must be https:// — refuses http:// (which
    ///     a tampered GH JSON could otherwise downgrade us to).
    ///   * Initial host AND every redirect host must be on the
    ///     allowlist (github.com or *.githubusercontent.com — GH
    ///     legitimately 302s from the API URL to the CDN).
    ///   * Auto-redirect is disabled; we follow redirects manually
    ///     with a hard 5-hop cap and a per-hop scheme + host check.
    ///   * Response buffer size is capped at MaxDllBytes so a
    ///     hostile chunked-encoding body can't OOM the host before
    ///     PluginUpdater's post-download size check fires.
    ///
    /// These are defence-in-depth — the SHA-256 verify in
    /// PluginUpdater is the actual gate that decides whether bytes
    /// get staged. But pinning the wire path costs ~30 lines and
    /// closes a class of "what if the GH JSON is tampered" scenarios
    /// where the digest verify would still succeed against a hostile
    /// download (e.g. attacker controls both the URL and the digest
    /// in the JSON response).
    /// </summary>
    public class HttpDllAssetFetcher : IDllAssetFetcher
    {
        private const int MaxRedirects = 5;

        private readonly HttpClient _http;
        private readonly string _userAgent;

        public HttpDllAssetFetcher(HttpClient http, string userAgent)
        {
            _http = http;
            _userAgent = string.IsNullOrWhiteSpace(userAgent) ? "EQ2LexiconACTPlugin" : userAgent;
            // Hard ceiling on response body — beat the post-download
            // length check so a 1 GB hostile body can't be fully read
            // into memory before we notice.
            try { _http.MaxResponseContentBufferSize = PluginUpdater.MaxDllBytes + 1; }
            catch { /* HttpClient may already have sent a request; ignore */ }
        }

        public async Task<byte[]> FetchAsync(string url)
        {
            Uri current;
            if (!Uri.TryCreate(url, UriKind.Absolute, out current))
                throw new InvalidOperationException("Asset URL is not a valid absolute URI.");
            ValidateAssetUri(current);

            // Manual redirect chain — re-check scheme and host at each
            // hop so a downgrade or off-domain redirect can't slip in.
            for (int hop = 0; hop < MaxRedirects; hop++)
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, current))
                {
                    req.Headers.UserAgent.ParseAdd(_userAgent);
                    using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                    {
                        var code = (int)resp.StatusCode;
                        if (code >= 300 && code < 400 && resp.Headers.Location != null)
                        {
                            var next = resp.Headers.Location.IsAbsoluteUri
                                ? resp.Headers.Location
                                : new Uri(current, resp.Headers.Location);
                            ValidateAssetUri(next);
                            current = next;
                            continue;
                        }
                        if (!resp.IsSuccessStatusCode)
                        {
                            throw new HttpRequestException(
                                $"HTTP {code} {resp.StatusCode} when fetching {current}");
                        }
                        // Pre-check Content-Length when the server
                        // gave us one — fail fast before allocating.
                        var clen = resp.Content.Headers.ContentLength;
                        if (clen.HasValue && clen.Value > PluginUpdater.MaxDllBytes)
                        {
                            throw new HttpRequestException(
                                $"Response Content-Length ({clen.Value}) exceeds cap.");
                        }
                        return await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    }
                }
            }
            throw new InvalidOperationException("Too many redirects fetching asset.");
        }

        /// <summary>
        /// Enforce https + the GitHub-domain allowlist on every URL
        /// we follow. Public + static so PluginUpdater tests can
        /// pin the rules without the HTTP machinery.
        /// </summary>
        public static void ValidateAssetUri(Uri uri)
        {
            if (uri == null) throw new InvalidOperationException("Asset URI is null.");
            if (uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException(
                    $"Asset URI scheme '{uri.Scheme}' rejected — only https:// is allowed.");
            if (!IsAllowedAssetHost(uri.Host))
                throw new InvalidOperationException(
                    $"Asset URI host '{uri.Host}' is not on the GitHub allowlist.");
        }

        /// <summary>
        /// Allowed hosts for GitHub release-asset downloads. GH
        /// release URLs start at github.com and 302 to the
        /// objects.githubusercontent.com CDN; both must be permitted.
        /// </summary>
        internal static bool IsAllowedAssetHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return false;
            var h = host.ToLowerInvariant();
            return h == "github.com"
                || h == "objects.githubusercontent.com"
                || h.EndsWith(".githubusercontent.com");
        }
    }

    /// <summary>
    /// Pure download + verify orchestration for the self-update flow.
    /// Lives in Core because the verification logic (SHA-256, constant-
    /// time compare, size cap) is exactly the kind of thing that
    /// silently regresses without tests.
    ///
    /// The actual on-disk staging happens in <c>UpdateInstaller</c>
    /// (UI assembly — needs Process.Start, etc.) because Core can't
    /// know where the running DLL is or how to spawn the swap helper.
    /// </summary>
    public static class PluginUpdater
    {
        /// <summary>
        /// Hard cap on the downloaded DLL size. Real DLL is ~76 KB; a
        /// 10 MB ceiling absorbs years of growth and still rejects a
        /// hostile substitution that would balloon the file (e.g. a
        /// CDN-cached different binary).
        /// </summary>
        public const int MaxDllBytes = 10 * 1024 * 1024;

        /// <summary>
        /// Download bytes from <paramref name="url"/>, compute SHA-256,
        /// constant-time-compare against <paramref name="expectedSha256Hex"/>.
        /// Returns a <see cref="DownloadResult"/> — caller decides
        /// what to do with success/failure (almost certainly: stage on
        /// disk or display the error message).
        ///
        /// Hard rule: <paramref name="expectedSha256Hex"/> must be
        /// non-empty (64 lowercase hex chars). We refuse to download
        /// without a digest to compare against — shipping unverified
        /// code into the user's ACT process would be an own-goal.
        /// </summary>
        public static async Task<DownloadResult> DownloadAndVerifyAsync(
            string url,
            string expectedSha256Hex,
            IDllAssetFetcher fetcher)
        {
            if (string.IsNullOrWhiteSpace(url))
                return new DownloadResult { Message = "No download URL available." };
            if (string.IsNullOrWhiteSpace(expectedSha256Hex))
                return new DownloadResult { Message = "No SHA-256 digest published for this release — refusing to auto-install." };
            if (fetcher == null)
                return new DownloadResult { Message = "Internal: fetcher missing." };

            byte[] bytes;
            try
            {
                bytes = await fetcher.FetchAsync(url).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new DownloadResult { Message = "Download failed: " + ex.Message };
            }

            if (bytes == null || bytes.Length == 0)
                return new DownloadResult { Message = "Download returned no bytes." };
            if (bytes.Length > MaxDllBytes)
                return new DownloadResult
                {
                    Message = $"Downloaded file too large ({bytes.Length} bytes; cap is {MaxDllBytes}).",
                };

            string actualHex;
            using (var sha = SHA256.Create())
            {
                actualHex = ToLowerHex(sha.ComputeHash(bytes));
            }
            var expected = expectedSha256Hex.Trim().ToLowerInvariant();
            if (!ConstantTimeEquals(actualHex, expected))
            {
                return new DownloadResult
                {
                    Message = $"SHA-256 mismatch (expected {expected.Substring(0, 12)}…, got {actualHex.Substring(0, 12)}…). " +
                              "Refusing to install a tampered or corrupt download.",
                };
            }

            return new DownloadResult
            {
                Success = true,
                DllBytes = bytes,
                Message = $"Downloaded {bytes.Length} bytes, SHA-256 verified.",
            };
        }

        private static string ToLowerHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
            {
                sb.Append(bytes[i].ToString("x2"));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Length-then-bitwise compare. Length difference short-circuits
        /// (and is itself not secret — hashes are fixed-width) but the
        /// per-char compare never short-circuits, so timing doesn't leak
        /// which prefix matched. Probably overkill for "did the download
        /// get tampered with" but free and correct.
        /// </summary>
        private static bool ConstantTimeEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++)
            {
                diff |= a[i] ^ b[i];
            }
            return diff == 0;
        }
    }
}
