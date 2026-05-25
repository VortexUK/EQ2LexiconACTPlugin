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
    /// Production fetcher — wraps HttpClient and applies the same
    /// User-Agent contract the rest of the plugin uses.
    /// </summary>
    public class HttpDllAssetFetcher : IDllAssetFetcher
    {
        private readonly HttpClient _http;
        private readonly string _userAgent;

        public HttpDllAssetFetcher(HttpClient http, string userAgent)
        {
            _http = http;
            _userAgent = string.IsNullOrWhiteSpace(userAgent) ? "EQ2LexiconACTPlugin" : userAgent;
        }

        public async Task<byte[]> FetchAsync(string url)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                req.Headers.UserAgent.ParseAdd(_userAgent);
                using (var resp = await _http.SendAsync(req).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode)
                    {
                        throw new HttpRequestException(
                            $"HTTP {(int)resp.StatusCode} {resp.StatusCode} when fetching {url}");
                    }
                    return await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                }
            }
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
