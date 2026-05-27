using System;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace EQ2Lexicon.ACTPlugin
{
    /// <summary>
    /// Anonymised summary of one outbound HTTP exchange. Surfaced via
    /// <see cref="UploadClient.RequestCompleted"/> so the settings panel
    /// can render an HTTP row in the event log. Intentionally carries
    /// NOTHING sensitive — no Bearer token, no payload body, no response
    /// body past a small excerpt — so the whole log can be safely copied
    /// to the clipboard or pasted into a bug report.
    /// </summary>
    public sealed class HttpExchangeInfo
    {
        public string Verb { get; set; } = "";
        public string Url { get; set; } = "";
        public int StatusCode { get; set; }
        public int DurationMs { get; set; }
        public bool Success { get; set; }
        /// <summary>First ~200 chars of the response body (sanitised). May be empty.</summary>
        public string ResponseExcerpt { get; set; } = "";
    }

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

        /// <summary>
        /// Fires after every HTTP request completes (success or failure).
        /// Subscribers receive an anonymised summary — never the token,
        /// never the payload body. Used by the settings-panel event log
        /// so the user can see what the plugin is doing over the wire.
        /// May fire on any thread; subscribers must marshal if they
        /// touch UI.
        /// </summary>
        public event Action<HttpExchangeInfo>? RequestCompleted;

        private void RaiseRequestCompleted(string verb, string url, int statusCode, long durationMs, bool success, string responseExcerpt)
        {
            var handler = RequestCompleted;
            if (handler == null) return;
            try
            {
                handler(new HttpExchangeInfo
                {
                    Verb = verb,
                    Url = url,
                    StatusCode = statusCode,
                    // Clamp to int — durations over ~24 days don't happen.
                    DurationMs = durationMs > int.MaxValue ? int.MaxValue : (int)durationMs,
                    Success = success,
                    ResponseExcerpt = responseExcerpt ?? "",
                });
            }
            catch
            {
                // Subscribers must never break the HTTP flow. Swallow any
                // exception they throw — telemetry is best-effort.
            }
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

            /// <summary>
            /// True when the server's whoami response asserted that the
            /// caller is a site admin. Default false — fails CLOSED for
            /// any path that wasn't a successful whoami round-trip
            /// (network error, old server without the field, non-2xx).
            /// Drives the URL-field editability gate in SettingsPanel.
            /// </summary>
            public bool IsAdmin { get; set; }

            /// <summary>
            /// EQ2 server names the user is permitted to upload parses
            /// from. Sourced from the whoami response's
            /// "allowed_servers" array. Null when no successful whoami
            /// has happened yet — Plugin substitutes its own defaults
            /// (currently Varsoon + Wuoshi) in that case so the UI
            /// always has something to display.
            /// </summary>
            public System.Collections.Generic.IReadOnlyList<string>? AllowedServers { get; set; }
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
            var sw = Stopwatch.StartNew();

            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);

                try
                {
                    using (var resp = await _http.SendAsync(req).ConfigureAwait(false))
                    {
                        var body = await ReadBodyUtf8Async(resp.Content).ConfigureAwait(false);
                        sw.Stop();
                        if (!resp.IsSuccessStatusCode)
                        {
                            RaiseRequestCompleted("GET", url, (int)resp.StatusCode, sw.ElapsedMilliseconds, false, Truncate(body, 200));
                            return new Result
                            {
                                Success = false,
                                StatusCode = (int)resp.StatusCode,
                                Message = $"Server responded {(int)resp.StatusCode} {resp.StatusCode}",
                                Body = Truncate(body, 500),
                            };
                        }
                        // Parse the JSON minimally for the friendly name,
                        // the admin flag, and the allowed-servers list.
                        // Admin defaults FALSE when the field is absent —
                        // fail-closed against an outdated server. The
                        // allowed-servers list is null when absent so the
                        // caller can distinguish "server didn't say" from
                        // "server explicitly said empty list".
                        var name = ExtractJsonString(body, "discord_name") ?? "(unknown)";
                        var admin = ExtractJsonBool(body, "is_admin") ?? false;
                        var allowed = ExtractJsonStringArray(body, "allowed_servers");
                        RaiseRequestCompleted("GET", url, (int)resp.StatusCode, sw.ElapsedMilliseconds, true, Truncate(body, 200));
                        return new Result
                        {
                            Success = true,
                            StatusCode = (int)resp.StatusCode,
                            Message = $"Connected as {name}.",
                            Body = body,
                            IsAdmin = admin,
                            AllowedServers = allowed,
                        };
                    }
                }
                catch (TaskCanceledException)
                {
                    sw.Stop();
                    RaiseRequestCompleted("GET", url, 0, sw.ElapsedMilliseconds, false, "timeout");
                    return new Result { Message = "Request timed out — server unreachable?" };
                }
                catch (HttpRequestException ex)
                {
                    sw.Stop();
                    RaiseRequestCompleted("GET", url, 0, sw.ElapsedMilliseconds, false, "network error");
                    return new Result { Message = "Network error: " + ex.Message };
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    RaiseRequestCompleted("GET", url, 0, sw.ElapsedMilliseconds, false, ex.GetType().Name);
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
            var sw = Stopwatch.StartNew();

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
                        sw.Stop();
                        if (!resp.IsSuccessStatusCode)
                        {
                            // Pull a friendly error out of FastAPI's {"detail": "..."} shape if present.
                            var detail = ExtractJsonString(body, "detail");
                            var msg = string.IsNullOrEmpty(detail)
                                ? $"Server responded {(int)resp.StatusCode} {resp.StatusCode}"
                                : $"{(int)resp.StatusCode}: {detail}";
                            RaiseRequestCompleted("POST", url, (int)resp.StatusCode, sw.ElapsedMilliseconds, false, Truncate(body, 200));
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
                        RaiseRequestCompleted("POST", url, (int)resp.StatusCode, sw.ElapsedMilliseconds, true, Truncate(body, 200));
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
                    sw.Stop();
                    RaiseRequestCompleted("POST", url, 0, sw.ElapsedMilliseconds, false, "timeout");
                    return new Result { Message = "Upload timed out — server unreachable?" };
                }
                catch (HttpRequestException ex)
                {
                    sw.Stop();
                    RaiseRequestCompleted("POST", url, 0, sw.ElapsedMilliseconds, false, "network error");
                    return new Result { Message = "Network error: " + ex.Message };
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    RaiseRequestCompleted("POST", url, 0, sw.ElapsedMilliseconds, false, ex.GetType().Name);
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
                        // sequence; we emit each char and System.String
                        // stores them as a valid UTF-16 surrogate pair.
                        //
                        // Validate each char is a real hex digit before
                        // TryParse — NumberStyles.HexNumber tolerates
                        // leading/trailing whitespace which would make
                        // "\u4f6 bar" silently parse as U+04F6.
                        //
                        // Reject lone surrogates explicitly. A hostile
                        // server sending "\uD83D" (high surrogate
                        // with no low) would otherwise produce an
                        // ill-formed UTF-16 string we then put into
                        // a WinForms label. WinForms tolerates lone
                        // surrogates but downstream code (clipboard
                        // copy, JSON re-serialise) may not.
                        ushort code;
                        if (!TryParseUnicodeEscape(json, i, out code)) return null;
                        if (char.IsLowSurrogate((char)code)) return null;
                        if (char.IsHighSurrogate((char)code))
                        {
                            // Need an immediately-following \uDC00-\uDFFF.
                            if (i + 11 >= json.Length) return null;
                            if (json[i + 6] != '\\' || json[i + 7] != 'u') return null;
                            ushort low;
                            if (!TryParseUnicodeEscape(json, i + 6, out low)) return null;
                            if (!char.IsLowSurrogate((char)low)) return null;
                            sb.Append((char)code);
                            sb.Append((char)low);
                            i += 11;  // past both \uXXXX\uYYYY
                        }
                        else
                        {
                            sb.Append((char)code);
                            i += 5;
                        }
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
        // Tiny JSON bool-field extractor. Sibling to ExtractJsonString —
        // pulls a single `"field": true|false` value out of a JSON body
        // without dragging in a full parser. Returns null when the field
        // is absent, or the value is non-bool (e.g. 0/1/"true"), or the
        // body is malformed near the field.
        //
        // Fail-CLOSED on ambiguity: any caller treating "null" as the
        // safer default gets that for free. The whoami admin gate
        // depends on this — an outdated server (no field) → null → user
        // treated as non-admin → URL field stays locked.
        // -------------------------------------------------------------------

        internal static bool? ExtractJsonBool(string json, string fieldName)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var marker = $"\"{fieldName}\"";
            var idx = json.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return null;
            idx = json.IndexOf(':', idx + marker.Length);
            if (idx < 0) return null;
            // Skip whitespace after the colon.
            int v = idx + 1;
            while (v < json.Length && char.IsWhiteSpace(json[v])) v++;
            if (v >= json.Length) return null;

            // Match `true` / `false` literally. Anything else (numbers,
            // strings, null, whitespace, comments) is not a bool — return
            // null so the caller falls back to the safe default.
            if (v + 4 <= json.Length && json[v] == 't'
                && json[v + 1] == 'r' && json[v + 2] == 'u' && json[v + 3] == 'e')
            {
                // Ensure the literal isn't a prefix of something longer
                // (e.g. "truely") — JSON tokens are delimited by
                // whitespace, `,`, `}`, or `]`.
                if (v + 4 == json.Length || IsJsonTokenBoundary(json[v + 4])) return true;
                return null;
            }
            if (v + 5 <= json.Length && json[v] == 'f'
                && json[v + 1] == 'a' && json[v + 2] == 'l'
                && json[v + 3] == 's' && json[v + 4] == 'e')
            {
                if (v + 5 == json.Length || IsJsonTokenBoundary(json[v + 5])) return false;
                return null;
            }
            return null;
        }

        private static bool IsJsonTokenBoundary(char c)
        {
            return char.IsWhiteSpace(c) || c == ',' || c == '}' || c == ']';
        }

        // -------------------------------------------------------------------
        // Tiny JSON array-of-strings extractor. Used for the whoami
        // response's `allowed_servers` list (which EQ2 servers the
        // user is permitted to upload parses from). Same fail-closed
        // posture as the other extractors:
        //   - field absent  → null  (caller substitutes a default)
        //   - empty array   → empty list (different from null)
        //   - non-string element anywhere → null (refuse the whole
        //     array rather than emitting a partial / sanitised view
        //     the user might mistake for the truth)
        //   - malformed near the field → null
        //
        // Bounds:
        //   - MaxArrayEntries (32) — refuses arrays longer than this
        //     so a hostile/buggy server can't blow up the UI with
        //     thousands of entries.
        //   - MaxEntryLength (64) — individual server names that
        //     exceed this are treated as malformed. EQ2 server names
        //     are short (Varsoon, Wuoshi, Kaladim) — nothing approaches
        //     this in practice.
        // -------------------------------------------------------------------

        private const int MaxArrayEntries = 32;
        private const int MaxEntryLength = 64;

        internal static System.Collections.Generic.IReadOnlyList<string>? ExtractJsonStringArray(string json, string fieldName)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var marker = $"\"{fieldName}\"";
            var idx = json.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return null;
            idx = json.IndexOf(':', idx + marker.Length);
            if (idx < 0) return null;
            // Skip whitespace after the colon.
            int v = idx + 1;
            while (v < json.Length && char.IsWhiteSpace(json[v])) v++;
            if (v >= json.Length || json[v] != '[') return null;
            v++; // past the '['

            var result = new System.Collections.Generic.List<string>();
            while (v < json.Length)
            {
                // Skip whitespace + leading commas.
                while (v < json.Length && (char.IsWhiteSpace(json[v]) || json[v] == ','))
                {
                    v++;
                }
                if (v >= json.Length) return null;
                if (json[v] == ']') return result;   // end of array

                // Expect a string literal next. Anything else (number,
                // bool, null, nested object/array) is refused.
                if (json[v] != '"') return null;

                int strStart = v + 1;
                var sb = new System.Text.StringBuilder();
                bool closed = false;
                for (int i = strStart; i < json.Length; i++)
                {
                    char ch = json[i];
                    if (ch == '"')
                    {
                        v = i + 1;
                        closed = true;
                        break;
                    }
                    if (ch == '\\')
                    {
                        // Same escape table as ExtractJsonString for
                        // consistency, but without the unicode-
                        // surrogate machinery — server names are ASCII
                        // by social convention. If a non-ASCII server
                        // ever appears we fall back to a literal
                        // backslash so the user at least sees the
                        // escaped form rather than silently losing the
                        // character.
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
                            default:
                                sb.Append('\\');
                                sb.Append(esc);
                                i++;
                                break;
                        }
                        continue;
                    }
                    sb.Append(ch);
                    if (sb.Length > MaxEntryLength) return null;
                }
                if (!closed) return null;

                var trimmed = sb.ToString().Trim();
                if (trimmed.Length > 0) result.Add(trimmed);
                if (result.Count > MaxArrayEntries) return null;
            }
            return null; // ran off the end without seeing ']'
        }

        /// <summary>
        /// Parse the 4 hex digits at position <paramref name="escapeStart"/>
        /// (the position of the `\` in `\uXXXX`) into a ushort code unit.
        /// Returns false on out-of-bounds or non-hex digits. Extracted so
        /// the surrogate-pair handling can call it twice cleanly.
        /// </summary>
        private static bool TryParseUnicodeEscape(string json, int escapeStart, out ushort code)
        {
            code = 0;
            if (escapeStart + 5 >= json.Length) return false;
            for (int h = escapeStart + 2; h < escapeStart + 6; h++)
            {
                char hc = json[h];
                if (!((hc >= '0' && hc <= '9')
                    || (hc >= 'a' && hc <= 'f')
                    || (hc >= 'A' && hc <= 'F')))
                {
                    return false;
                }
            }
            var hex = json.Substring(escapeStart + 2, 4);
            return ushort.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out code);
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
