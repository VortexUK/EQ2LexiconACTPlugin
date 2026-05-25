using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace EQ2Lexicon.ACTPlugin
{
    /// <summary>
    /// Thin HttpClient wrapper around the EQ2 Lexicon site API. One
    /// instance per Plugin lifetime — keeps connection pooling working
    /// instead of recreating sockets per upload.
    /// </summary>
    public class UploadClient : IDisposable
    {
        private readonly HttpClient _http;

        /// <summary>
        /// Plugin assigns this after the first update check completes.
        /// When <see cref="UpdateCheckResult.UploadBlocked"/> is true,
        /// <see cref="UploadEncounterAsync"/> short-circuits with a
        /// "too old" error before sending. Null means "haven't checked
        /// yet" — uploads still proceed so a network outage at startup
        /// doesn't silently block parses.
        /// </summary>
        public UpdateCheckResult? UpdateStatus { get; set; }

        public UploadClient()
        {
            _http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(20),
            };
            // UA includes the assembly version so the server can see what
            // plugin version each upload came from. Useful for telemetry
            // and for the eventual server-side strict-version gate.
            var v = UpdateChecker.GetCurrentAssemblyVersion().ToString(3);
            _http.DefaultRequestHeaders.UserAgent.ParseAdd($"EQ2LexiconACTPlugin/{v}");
        }

        public void Dispose()
        {
            _http.Dispose();
        }

        public class Result
        {
            public bool Success { get; set; }
            public int StatusCode { get; set; }
            /// <summary>Short user-facing message — what to show in the UI.</summary>
            public string Message { get; set; } = "";
            /// <summary>Raw response body (truncated); useful when something fails.</summary>
            public string Body { get; set; } = "";
        }

        /// <summary>
        /// Hard cap for the upload body. JavaScriptSerializer caps at 8 MB
        /// on the build side, but we belt-and-brace it here so an unexpected
        /// caller (or a corrupted snapshot) can't push a multi-hundred-MB
        /// body over the wire.
        /// </summary>
        private const int MaxPayloadBytes = 10 * 1024 * 1024;

        /// <summary>
        /// Validates the user-entered server URL. Returns null when OK, or a
        /// human-readable error string when not. Allows https:// anywhere and
        /// http:// only for localhost (so a self-hosted dev backend on the
        /// loopback still works). Rejects other schemes (`file://`,
        /// `javascript:`, etc.) and malformed URIs — those would otherwise
        /// either leak the bearer token in plaintext or feed garbage to
        /// HttpClient.
        /// </summary>
        internal static string? ValidateServerUrl(string? serverUrl)
        {
            if (string.IsNullOrWhiteSpace(serverUrl))
                return "Server URL is empty.";
            Uri? uri;
            try
            {
                uri = new Uri(serverUrl, UriKind.Absolute);
            }
            catch (UriFormatException)
            {
                return "Server URL is malformed.";
            }
            if (uri.Scheme == Uri.UriSchemeHttps) return null;
            if (uri.Scheme == Uri.UriSchemeHttp)
            {
                var host = uri.Host.ToLowerInvariant();
                if (host == "localhost" || host == "127.0.0.1" || host == "[::1]")
                    return null;
                return "Server URL must use https:// (plain http is only allowed for localhost).";
            }
            return $"Server URL scheme '{uri.Scheme}' not supported — use https://.";
        }

        /// <summary>
        /// GET /api/auth/whoami with the bearer token. Returns Success=true
        /// when the server resolves the token to a user, with Message
        /// containing the resolved Discord name.
        /// </summary>
        public async Task<Result> TestConnectionAsync(string serverUrl, string apiToken)
        {
            var urlError = ValidateServerUrl(serverUrl);
            if (urlError != null) return new Result { Message = urlError };
            if (string.IsNullOrWhiteSpace(apiToken))
                return new Result { Message = "API token is empty." };

            var url = serverUrl.TrimEnd('/') + "/api/auth/whoami";

            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);

                try
                {
                    using (var resp = await _http.SendAsync(req).ConfigureAwait(false))
                    {
                        var body = await ReadBodyUtf8Async(resp.Content).ConfigureAwait(false);
                        if (!resp.IsSuccessStatusCode)
                        {
                            return new Result
                            {
                                Success = false,
                                StatusCode = (int)resp.StatusCode,
                                Message = $"Server responded {(int)resp.StatusCode} {resp.StatusCode}",
                                Body = Truncate(body, 500),
                            };
                        }
                        // Parse the JSON minimally for the friendly name.
                        var name = ExtractJsonString(body, "discord_name") ?? "(unknown)";
                        return new Result
                        {
                            Success = true,
                            StatusCode = (int)resp.StatusCode,
                            Message = $"Connected as {name}.",
                            Body = body,
                        };
                    }
                }
                catch (TaskCanceledException)
                {
                    return new Result { Message = "Request timed out — server unreachable?" };
                }
                catch (HttpRequestException ex)
                {
                    return new Result { Message = "Network error: " + ex.Message };
                }
                catch (Exception ex)
                {
                    return new Result { Message = "Unexpected error: " + ex.Message };
                }
            }
        }

        /// <summary>
        /// POST /api/parses/ingest with the bearer token and the captured
        /// JSON payload. Returns Success=true on 201, plus the server's
        /// response body for the UI to surface.
        /// </summary>
        public async Task<Result> UploadEncounterAsync(string serverUrl, string apiToken, string payloadJson)
        {
            // Gate before we touch the network: if this plugin is too far
            // behind the latest release, refuse to upload. Surface a
            // user-actionable message so the settings panel can prompt them
            // to update. Null UpdateStatus = "not yet checked" — allow.
            if (UpdateStatus != null && UpdateStatus.UploadBlocked)
            {
                return new Result
                {
                    Success = false,
                    Message = $"Plugin v{UpdateStatus.CurrentVersion} is too old (latest v{UpdateStatus.LatestVersion}). Update to continue uploading.",
                };
            }

            var urlError = ValidateServerUrl(serverUrl);
            if (urlError != null) return new Result { Message = urlError };
            if (string.IsNullOrWhiteSpace(apiToken))
                return new Result { Message = "API token is empty." };
            if (string.IsNullOrWhiteSpace(payloadJson))
                return new Result { Message = "Payload is empty." };
            if (payloadJson.Length > MaxPayloadBytes)
                return new Result
                {
                    Message = $"Payload too large ({payloadJson.Length} bytes; cap is {MaxPayloadBytes}).",
                };

            var url = serverUrl.TrimEnd('/') + "/api/parses/ingest";

            using (var req = new HttpRequestMessage(HttpMethod.Post, url))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
                req.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");

                // HMAC the body with the API token as the key. The server
                // recomputes and rejects if it doesn't match — see
                // PayloadSigner for the threat-model caveats.
                try
                {
                    var signature = PayloadSigner.Sign(payloadJson, apiToken);
                    req.Headers.Add(PayloadSigner.SignatureHeaderName, signature);
                }
                catch (ArgumentException)
                {
                    // Defensive: apiToken was validated above so this
                    // shouldn't fire. If it ever does, fail closed rather
                    // than send an unsigned body.
                    return new Result { Message = "Could not sign payload (token missing)." };
                }

                try
                {
                    using (var resp = await _http.SendAsync(req).ConfigureAwait(false))
                    {
                        var body = await ReadBodyUtf8Async(resp.Content).ConfigureAwait(false);
                        if (!resp.IsSuccessStatusCode)
                        {
                            // Pull a friendly error out of FastAPI's {"detail": "..."} shape if present.
                            var detail = ExtractJsonString(body, "detail");
                            var msg = string.IsNullOrEmpty(detail)
                                ? $"Server responded {(int)resp.StatusCode} {resp.StatusCode}"
                                : $"{(int)resp.StatusCode}: {detail}";
                            return new Result
                            {
                                Success = false,
                                StatusCode = (int)resp.StatusCode,
                                Message = msg,
                                Body = Truncate(body, 500),
                            };
                        }

                        // 201 — server returns status ('inserted' | 'skipped') + counts.
                        var status = ExtractJsonString(body, "status") ?? "ok";
                        return new Result
                        {
                            Success = true,
                            StatusCode = (int)resp.StatusCode,
                            Message = $"Uploaded ({status}).",
                            Body = body,
                        };
                    }
                }
                catch (TaskCanceledException)
                {
                    return new Result { Message = "Upload timed out — server unreachable?" };
                }
                catch (HttpRequestException ex)
                {
                    return new Result { Message = "Network error: " + ex.Message };
                }
                catch (Exception ex)
                {
                    return new Result { Message = "Unexpected error: " + ex.Message };
                }
            }
        }

        // -------------------------------------------------------------------
        // Tiny JSON string-field extractor — avoids pulling in a full JSON
        // parser just for one field. NOT a general-purpose JSON parser.
        //
        // Handles all the escape sequences defined in RFC 8259 § 7:
        //   \" \\ \/ \b \f \n \r \t \uXXXX
        // The \uXXXX path is the one that matters for non-ASCII Discord
        // names if a server is ever configured with ensure_ascii=True —
        // without it, "discord_name": "你好" came out as the
        // literal text "u4f60u597d" in the test-connection label.
        // -------------------------------------------------------------------

        internal static string? ExtractJsonString(string json, string fieldName)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var marker = $"\"{fieldName}\"";
            var idx = json.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return null;
            idx = json.IndexOf(':', idx + marker.Length);
            if (idx < 0) return null;
            // Skip whitespace
            while (idx + 1 < json.Length && char.IsWhiteSpace(json[idx + 1])) idx++;
            if (idx + 1 >= json.Length || json[idx + 1] != '"') return null;
            int start = idx + 2;
            var sb = new StringBuilder();
            for (int i = start; i < json.Length; i++)
            {
                char ch = json[i];
                if (ch == '"') return sb.ToString();
                if (ch != '\\')
                {
                    sb.Append(ch);
                    continue;
                }
                // Escape sequence — need at least one more char.
                if (i + 1 >= json.Length) return null;
                char esc = json[i + 1];
                switch (esc)
                {
                    case '"': sb.Append('"'); i++; break;
                    case '\\': sb.Append('\\'); i++; break;
                    case '/': sb.Append('/'); i++; break;
                    case 'b': sb.Append('\b'); i++; break;
                    case 'f': sb.Append('\f'); i++; break;
                    case 'n': sb.Append('\n'); i++; break;
                    case 'r': sb.Append('\r'); i++; break;
                    case 't': sb.Append('\t'); i++; break;
                    case 'u':
                        // \uXXXX — 4 hex digits → 1 char. Surrogate
                        // pairs (e.g. emoji) come as two \uXXXX in
                        // sequence; we just emit each char, and
                        // System.String stores them correctly as a
                        // UTF-16 surrogate pair.
                        //
                        // Validate each char is a real hex digit before
                        // TryParse — NumberStyles.HexNumber tolerates
                        // leading/trailing whitespace which would make
                        // "\u4f6 bar" silently parse as U+04F6.
                        if (i + 5 >= json.Length) return null;
                        for (int h = i + 2; h < i + 6; h++)
                        {
                            char hc = json[h];
                            if (!((hc >= '0' && hc <= '9')
                                || (hc >= 'a' && hc <= 'f')
                                || (hc >= 'A' && hc <= 'F')))
                            {
                                return null;
                            }
                        }
                        var hex = json.Substring(i + 2, 4);
                        if (!ushort.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                                System.Globalization.CultureInfo.InvariantCulture, out var code))
                        {
                            return null;
                        }
                        sb.Append((char)code);
                        i += 5;
                        break;
                    default:
                        // Unknown escape — preserve the backslash so the
                        // user at least sees something they can report.
                        sb.Append('\\');
                        sb.Append(esc);
                        i++;
                        break;
                }
            }
            return null;
        }

        // -------------------------------------------------------------------
        // .NET Framework's HttpContent.ReadAsStringAsync has a long-standing
        // charset-fallback issue when Content-Type lacks a charset directive.
        // FastAPI's JSONResponse intentionally omits the charset (the body
        // is always UTF-8 per RFC 8259), which hits the fallback path on
        // 4.8 and can mis-decode non-ASCII names. Read bytes + decode
        // explicitly as UTF-8 — same result on every framework version.
        //   https://github.com/dotnet/runtime/issues/28658
        // -------------------------------------------------------------------

        internal static async Task<string> ReadBodyUtf8Async(HttpContent content)
        {
            if (content == null) return "";
            var bytes = await content.ReadAsByteArrayAsync().ConfigureAwait(false);
            if (bytes == null || bytes.Length == 0) return "";
            return Encoding.UTF8.GetString(bytes);
        }

        internal static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }
}
