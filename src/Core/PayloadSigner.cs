using System;
using System.Security.Cryptography;
using System.Text;

namespace EQ2Lexicon.ACTPlugin
{
    /// <summary>
    /// HMAC-SHA256 over the upload body, keyed by the user's API token.
    /// The signature is sent as the <c>X-Lexicon-Signature</c> header and
    /// the server recomputes + compares with constant-time equality.
    ///
    /// ── What this defends ───────────────────────────────────────────────
    /// The intended threat is "casual payload tampering" — a user (or a
    /// MITM with the user's TLS session somehow) editing the JSON between
    /// what the plugin built and what the server stored, hoping to inflate
    /// their numbers. With HMAC the server can detect that the body
    /// changed after the holder of the API token signed it.
    ///
    /// ── What this does NOT defend ───────────────────────────────────────
    /// The legitimate holder of an API token can sign ANY payload — they
    /// have the key. So this does not, and cannot, prevent a determined
    /// user from forging a parse for themselves. Real integrity has to
    /// come from server-side sanity checks (cap-vs-level checks, encounter
    /// duration plausibility, cross-validation against other parses of the
    /// same encounter, etc.) which live in the EQ2 Lexicon repo.
    ///
    /// Choosing the API token as the HMAC key (rather than an embedded
    /// per-plugin secret) means:
    ///   - No secret to manage / rotate / leak via DLL disassembly.
    ///   - Tampering by someone who steals only the token still requires
    ///     them to recompute the signature using the protocol described
    ///     here — small extra friction over "just curl with Bearer".
    ///   - The plugin holds the API token in memory already, so no new
    ///     attack surface is added.
    /// </summary>
    public static class PayloadSigner
    {
        /// <summary>
        /// HTTP header name used to ship the signature to the server.
        /// Centralised here so plugin + server tests can reference the
        /// same string and a rename can't desync.
        /// </summary>
        public const string SignatureHeaderName = "X-Lexicon-Signature";

        /// <summary>
        /// Compute the HMAC-SHA256 of <paramref name="body"/> keyed by
        /// <paramref name="apiToken"/>. Returns a lower-case hex string.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="apiToken"/> is empty/whitespace.
        /// Caller MUST have validated the token before reaching here;
        /// signing with an empty key would produce a misleadingly
        /// authoritative-looking signature.
        /// </exception>
        public static string Sign(string body, string apiToken)
        {
            if (string.IsNullOrWhiteSpace(apiToken))
                throw new ArgumentException("API token is required to sign the payload.", nameof(apiToken));

            var keyBytes = Encoding.UTF8.GetBytes(apiToken);
            var bodyBytes = Encoding.UTF8.GetBytes(body ?? "");
            using (var hmac = new HMACSHA256(keyBytes))
            {
                var hash = hmac.ComputeHash(bodyBytes);
                return BytesToHex(hash);
            }
        }

        /// <summary>
        /// Lowercase-hex with no separator. Standard format for sig headers
        /// (matches what e.g. GitHub uses for `X-Hub-Signature-256`).
        /// </summary>
        private static string BytesToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
            {
                sb.Append(bytes[i].ToString("x2"));
            }
            return sb.ToString();
        }
    }
}
