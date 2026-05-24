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

        public UploadClient()
        {
            _http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(20),
            };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("EQ2LexiconACTPlugin/0.1.0");
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
        /// GET /api/auth/whoami with the bearer token. Returns Success=true
        /// when the server resolves the token to a user, with Message
        /// containing the resolved Discord name.
        /// </summary>
        public async Task<Result> TestConnectionAsync(string serverUrl, string apiToken)
        {
            if (string.IsNullOrWhiteSpace(serverUrl))
                return new Result { Message = "Server URL is empty." };
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
                        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
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
            if (string.IsNullOrWhiteSpace(serverUrl))
                return new Result { Message = "Server URL is empty." };
            if (string.IsNullOrWhiteSpace(apiToken))
                return new Result { Message = "API token is empty." };
            if (string.IsNullOrWhiteSpace(payloadJson))
                return new Result { Message = "Payload is empty." };

            var url = serverUrl.TrimEnd('/') + "/api/parses/ingest";

            using (var req = new HttpRequestMessage(HttpMethod.Post, url))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
                req.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");

                try
                {
                    using (var resp = await _http.SendAsync(req).ConfigureAwait(false))
                    {
                        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
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
                if (ch == '\\' && i + 1 < json.Length)
                {
                    sb.Append(json[i + 1]);
                    i++;
                    continue;
                }
                if (ch == '"') return sb.ToString();
                sb.Append(ch);
            }
            return null;
        }

        internal static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }
}
