using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace EQ2Lexicon.ACTPlugin
{
    /// <summary>
    /// Abstraction over the GitHub release-list fetch so unit tests can
    /// supply canned JSON without going to the network.
    /// </summary>
    public interface IReleaseFeedFetcher
    {
        Task<string> FetchAsync();
    }

    /// <summary>
    /// Production fetcher — hits the unauthenticated GitHub releases API.
    /// 60 req/h/IP limit is plenty since we cache for 24h in PluginConfig.
    /// </summary>
    public class GitHubReleaseFetcher : IReleaseFeedFetcher
    {
        public const string ReleasesApiUrl =
            "https://api.github.com/repos/VortexUK/EQ2LexiconACTPlugin/releases";

        private readonly HttpClient _http;
        private readonly string _userAgent;

        public GitHubReleaseFetcher(HttpClient http, string userAgent)
        {
            _http = http;
            // GitHub requires a User-Agent — they 403 anonymous-UA requests.
            _userAgent = string.IsNullOrWhiteSpace(userAgent) ? "EQ2LexiconACTPlugin" : userAgent;
        }

        public async Task<string> FetchAsync()
        {
            using (var req = new HttpRequestMessage(HttpMethod.Get, ReleasesApiUrl))
            {
                req.Headers.UserAgent.ParseAdd(_userAgent);
                // Per GitHub API conventions — pins the schema so a future
                // breaking change to the default response shape can't
                // silently confuse the parser below.
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                using (var resp = await _http.SendAsync(req).ConfigureAwait(false))
                {
                    // Don't throw on non-200 — let the caller decide. A 403
                    // (rate-limit) or 5xx shouldn't kill the plugin; we just
                    // keep the previous cached tags and try again next time.
                    if (!resp.IsSuccessStatusCode) return "";
                    return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// Pure version-comparison helpers + the orchestration that calls a
    /// fetcher and produces an <see cref="UpdateCheckResult"/>.
    ///
    /// Split out from <see cref="UploadClient"/> and <see cref="Plugin"/>
    /// because the logic is small, easy to get wrong (off-by-one on the
    /// stale threshold; pre-release tag handling; dev-build detection),
    /// and a lot more useful when it's covered by tests that don't need
    /// the network.
    /// </summary>
    public static class UpdateChecker
    {
        /// <summary>
        /// Number of releases behind latest we tolerate before flipping
        /// <see cref="UpdateStatus.SlightlyStale"/> → <see cref="UpdateStatus.TooOld"/>.
        ///
        /// 2 = the user can be ONE release behind without any warning at
        /// upgrade time, TWO releases behind with a yellow nudge, and only
        /// blocked from THREE+ behind. Picked to be tolerant of "I'll get
        /// to it next week" while still preventing months-stale installs
        /// from producing payloads we no longer test against.
        /// </summary>
        public const int StaleThresholdVersions = 2;

        /// <summary>
        /// Parse the GitHub releases API response into version objects,
        /// newest first. Tolerant of: empty/malformed JSON (returns []),
        /// non-numeric tags (skipped), draft releases (skipped — they
        /// have a `"draft": true` flag).
        ///
        /// We don't pull in System.Text.Json (.NET Standard 2.0 / net48 has
        /// JavaScriptSerializer + the in-tree hand-rolled extractors we
        /// already use). The shape we need is small: every release object
        /// has `"tag_name": "vX.Y.Z"`. We grep for those.
        /// </summary>
        public static List<Version> ParseReleaseVersions(string json)
        {
            var result = new List<Version>();
            if (string.IsNullOrEmpty(json)) return result;

            // Walk the JSON looking for top-level `"tag_name": "..."` strings.
            // Releases come back newest-first from GitHub so we preserve
            // that order. We also check for an adjacent `"draft": true` to
            // skip draft releases (which a maintainer could accidentally
            // leave around — we should not gate users on a draft tag).
            int idx = 0;
            while (idx < json.Length)
            {
                int tagAt = json.IndexOf("\"tag_name\"", idx, StringComparison.Ordinal);
                if (tagAt < 0) break;
                int colon = json.IndexOf(':', tagAt);
                if (colon < 0) break;
                int quoteOpen = json.IndexOf('"', colon + 1);
                if (quoteOpen < 0) break;
                int quoteClose = json.IndexOf('"', quoteOpen + 1);
                if (quoteClose < 0) break;
                var tag = json.Substring(quoteOpen + 1, quoteClose - quoteOpen - 1);
                idx = quoteClose + 1;

                // Check the next ~200 chars for a draft flag belonging to
                // the same release object. Drafts have `"draft": true`.
                int probeEnd = Math.Min(json.Length, idx + 400);
                var probe = json.Substring(idx, probeEnd - idx);
                if (probe.IndexOf("\"draft\"", StringComparison.Ordinal) is int draftAt && draftAt >= 0)
                {
                    int draftColon = probe.IndexOf(':', draftAt);
                    if (draftColon > 0)
                    {
                        var after = probe.Substring(draftColon + 1).TrimStart();
                        if (after.StartsWith("true", StringComparison.OrdinalIgnoreCase))
                        {
                            continue; // skip drafts
                        }
                    }
                }

                var v = TryParseTag(tag);
                if (v != null) result.Add(v);
            }
            return result;
        }

        /// <summary>
        /// Walk the GitHub release feed for the FIRST non-draft release
        /// (newest published) and pull out the URL + SHA-256 digest of
        /// the EQ2Lexicon.ACTPlugin.dll asset attached to it.
        ///
        /// Used by the self-update flow (v0.1.12+) — the URL feeds
        /// HttpClient.GetByteArrayAsync, the digest feeds
        /// PluginUpdater's hash verification.
        ///
        /// Returns empty strings when:
        ///   * JSON is empty / malformed
        ///   * no non-draft release exists
        ///   * the release has no asset matching "*.ACTPlugin.dll"
        ///   * the asset has no `digest` field (very recent GitHub
        ///     releases occasionally surface before the digest backfill
        ///     completes) — the installer must refuse to auto-stage
        ///     in that case rather than ship an unverified binary
        ///
        /// Parser is a small hand-rolled walk over the JSON shape rather
        /// than full deserialisation, matching the convention used by
        /// ParseReleaseVersions. The shape we depend on:
        ///   [ { "tag_name": "v0.1.12", "draft": false,
        ///       "assets": [ { "name": "...dll",
        ///                     "browser_download_url": "https://...",
        ///                     "digest": "sha256:abc..." } ] } ]
        /// </summary>
        public static (string Url, string Sha256Hex) ParseLatestDllAsset(string json)
        {
            if (string.IsNullOrEmpty(json)) return ("", "");

            // Find the first release block — defined as the first
            // `"tag_name"` whose corresponding `"draft"` is false (or
            // absent). The block runs until the matching closing brace
            // at the top object level. We approximate that as "the
            // next `"tag_name"`" — assets always appear before the
            // next release object since GH returns assets inline.
            int idx = 0;
            while (idx < json.Length)
            {
                int tagAt = json.IndexOf("\"tag_name\"", idx, StringComparison.Ordinal);
                if (tagAt < 0) return ("", "");
                // Block end = the next "tag_name" (next release) or end of JSON.
                int nextTagAt = json.IndexOf("\"tag_name\"", tagAt + 1, StringComparison.Ordinal);
                int blockEnd = nextTagAt < 0 ? json.Length : nextTagAt;
                var block = json.Substring(tagAt, blockEnd - tagAt);
                idx = blockEnd;

                // Skip drafts.
                if (LooksLikeDraft(block)) continue;

                // Find the assets array and within it the first asset
                // whose `"name"` ends with ".dll" (we ship exactly one
                // DLL today; matching by suffix accommodates future
                // multi-asset releases).
                var (url, sha) = FindDllAsset(block);
                return (url, sha);
            }
            return ("", "");
        }

        private static bool LooksLikeDraft(string block)
        {
            int draftAt = block.IndexOf("\"draft\"", StringComparison.Ordinal);
            if (draftAt < 0) return false;
            int colon = block.IndexOf(':', draftAt);
            if (colon < 0) return false;
            var after = block.Substring(colon + 1).TrimStart();
            return after.StartsWith("true", StringComparison.OrdinalIgnoreCase);
        }

        private static (string Url, string Sha256Hex) FindDllAsset(string releaseBlock)
        {
            // Scope the search to the `"assets":[ ... ]` array so we
            // don't accidentally pick up `"author":{"name":"foo.dll"}`
            // or `"uploader":{"name":"x.dll"}` — both legal GitHub
            // username/display-name shapes that would otherwise hijack
            // the DLL-name match. See the v0.1.13 audit M1 finding.
            var assetsBlock = ExtractAssetsArray(releaseBlock);
            if (assetsBlock.Length == 0) return ("", "");

            // Walk every `"name"` field in the assets array; for each
            // one ending ".dll", pluck the adjacent
            // browser_download_url + digest. GitHub orders the JSON
            // keys consistently inside each asset object so the
            // adjacent search is stable.
            int idx = 0;
            while (idx < assetsBlock.Length)
            {
                int nameAt = assetsBlock.IndexOf("\"name\"", idx, StringComparison.Ordinal);
                if (nameAt < 0) return ("", "");
                var nameVal = UploadClient.ExtractJsonString(assetsBlock.Substring(nameAt), "name") ?? "";
                idx = nameAt + "\"name\"".Length;

                if (!nameVal.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;

                // Found a DLL asset. The browser_download_url + digest
                // for THIS asset are within the next ~1KB of JSON.
                int probeEnd = Math.Min(assetsBlock.Length, idx + 1500);
                var probe = assetsBlock.Substring(idx, probeEnd - idx);
                var url = UploadClient.ExtractJsonString(probe, "browser_download_url") ?? "";
                var digest = UploadClient.ExtractJsonString(probe, "digest") ?? "";

                // Digest is shaped "sha256:HEX..." — strip the algo prefix.
                var sha = "";
                if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                {
                    sha = digest.Substring("sha256:".Length).Trim().ToLowerInvariant();
                }
                return (url, sha);
            }
            return ("", "");
        }

        /// <summary>
        /// Locate the `"assets":[ ... ]` array within a single release
        /// object and return its content (between `[` and the matching
        /// `]`). Uses bracket-counting so nested objects inside an
        /// asset don't trip the matcher. Returns "" if no assets array
        /// is found or the bracket structure is malformed.
        /// </summary>
        private static string ExtractAssetsArray(string releaseBlock)
        {
            if (string.IsNullOrEmpty(releaseBlock)) return "";
            int assetsAt = releaseBlock.IndexOf("\"assets\"", StringComparison.Ordinal);
            if (assetsAt < 0) return "";
            int colon = releaseBlock.IndexOf(':', assetsAt);
            if (colon < 0) return "";
            // Skip whitespace to find the `[`.
            int i = colon + 1;
            while (i < releaseBlock.Length && char.IsWhiteSpace(releaseBlock[i])) i++;
            if (i >= releaseBlock.Length || releaseBlock[i] != '[') return "";
            int arrayStart = i + 1;

            // Bracket-count to find the matching `]`. Must respect
            // strings (so a `]` inside a release-note URL doesn't
            // close the array prematurely) and escapes inside them.
            int depth = 1;
            bool inString = false;
            for (int j = arrayStart; j < releaseBlock.Length; j++)
            {
                char c = releaseBlock[j];
                if (inString)
                {
                    if (c == '\\' && j + 1 < releaseBlock.Length) { j++; continue; }
                    if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; continue; }
                if (c == '[') depth++;
                else if (c == ']')
                {
                    depth--;
                    if (depth == 0) return releaseBlock.Substring(arrayStart, j - arrayStart);
                }
            }
            return ""; // malformed — unclosed array
        }

        /// <summary>
        /// Convert "v0.1.8" or "0.1.8" (with optional "-rc1" suffix) into
        /// a <see cref="Version"/>. Returns null on anything we can't make
        /// sense of so the caller can filter.
        /// </summary>
        public static Version? TryParseTag(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return null;
            var s = tag.Trim();
            if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s.Substring(1);
            // Drop -rc1 / -beta / +build suffixes — System.Version doesn't
            // grok semver pre-release; we treat them as the same version
            // for comparison purposes. (If we ever ship a release-candidate
            // *as* an official tag, this means it'll be considered equal
            // to the eventual non-rc release — that's fine for our
            // staleness model.)
            int dashAt = s.IndexOf('-');
            if (dashAt >= 0) s = s.Substring(0, dashAt);
            int plusAt = s.IndexOf('+');
            if (plusAt >= 0) s = s.Substring(0, plusAt);
            return Version.TryParse(s, out var v) ? v : null;
        }

        /// <summary>
        /// Decide where <paramref name="currentVersion"/> sits relative to
        /// <paramref name="releasedVersions"/> (the GitHub feed, newest
        /// first).
        ///
        /// - <see cref="UpdateStatus.Unknown"/> if the feed is empty.
        /// - <see cref="UpdateStatus.DevBuild"/> if current is strictly
        ///   greater than the latest published (i.e. we're running a build
        ///   that hasn't been tagged yet — happens in dev + on the release
        ///   branch between bump-and-tag).
        /// - <see cref="UpdateStatus.Current"/> if current equals the latest.
        /// - <see cref="UpdateStatus.SlightlyStale"/> if current is within
        ///   the top (<see cref="StaleThresholdVersions"/> + 1) versions.
        /// - <see cref="UpdateStatus.TooOld"/> otherwise.
        /// </summary>
        public static UpdateCheckResult Compute(Version currentVersion, IList<Version> releasedVersions)
        {
            // Normalise the caller's current version to 3-part so
            // Version.Equals matches against the 3-part tag-parsed
            // released-version list. See GetCurrentAssemblyVersion
            // for the full reasoning — defensive double-application
            // here so this function is correct in isolation too.
            var normalizedCurrent = currentVersion == null
                ? new Version(0, 0, 0)
                : NormalizeToThreePart(currentVersion);

            var result = new UpdateCheckResult
            {
                CurrentVersion = normalizedCurrent.ToString(3),
            };

            if (releasedVersions == null || releasedVersions.Count == 0)
            {
                result.Status = UpdateStatus.Unknown;
                return result;
            }

            // Sort descending so [0] is the newest, regardless of what
            // the caller passed in. GitHub already returns newest-first
            // but we don't rely on that here. Normalise each entry too
            // in case a caller mixes shapes.
            var sorted = releasedVersions
                .Where(v => v != null)
                .Select(NormalizeToThreePart)
                .Distinct()
                .OrderByDescending(v => v)
                .ToList();

            if (sorted.Count == 0)
            {
                result.Status = UpdateStatus.Unknown;
                return result;
            }

            var latest = sorted[0];
            result.LatestVersion = latest.ToString(3);
            result.LatestReleaseUrl =
                $"https://github.com/VortexUK/EQ2LexiconACTPlugin/releases/tag/v{result.LatestVersion}";

            if (currentVersion == null)
            {
                result.Status = UpdateStatus.Unknown;
                return result;
            }

            int cmp = normalizedCurrent.CompareTo(latest);
            if (cmp > 0)
            {
                result.Status = UpdateStatus.DevBuild;
                return result;
            }
            if (cmp == 0)
            {
                result.Status = UpdateStatus.Current;
                return result;
            }

            // Current is older. Where in the recent list does it sit?
            // Tolerant: SlightlyStale means within the top (Threshold+1)
            // released versions — so threshold=2 covers latest + the two
            // before it, which together are "current or within 2 back".
            // IndexOf uses Version.Equals which compares ALL components,
            // hence the normalisation on both sides above.
            int allowedRecent = StaleThresholdVersions + 1;
            int idxInRecent = sorted.Take(allowedRecent).ToList().IndexOf(normalizedCurrent);
            result.Status = idxInRecent >= 0 ? UpdateStatus.SlightlyStale : UpdateStatus.TooOld;
            return result;
        }

        /// <summary>
        /// End-to-end: fetch, parse, compute. Returns <see cref="UpdateStatus.Unknown"/>
        /// on any failure (network, parse, etc.) so the caller can keep
        /// running without special-casing each exception type.
        /// </summary>
        public static async Task<UpdateCheckResult> CheckAsync(Version currentVersion, IReleaseFeedFetcher fetcher)
        {
            var result = new UpdateCheckResult
            {
                CurrentVersion = currentVersion?.ToString(3) ?? "",
                Status = UpdateStatus.Unknown,
            };
            if (fetcher == null) return result;
            try
            {
                var json = await fetcher.FetchAsync().ConfigureAwait(false);
                if (string.IsNullOrEmpty(json)) return result;
                var versions = ParseReleaseVersions(json);
                var status = Compute(currentVersion ?? new Version(0, 0, 0), versions);

                // Latest-release asset info for the self-update flow.
                // Best-effort: missing/malformed asset metadata leaves
                // both strings empty and the installer falls back to
                // opening the browser instead of staging.
                var (url, sha) = ParseLatestDllAsset(json);
                status.LatestDllUrl = url;
                status.LatestDllSha256 = sha;

                return status;
            }
            catch
            {
                // Update checks are best-effort. A failure here must not
                // crash the plugin or break uploads — Unknown is the
                // sentinel that says "proceed without gating".
                return result;
            }
        }

        /// <summary>
        /// Pulls the assembly's runtime version. Centralised so the
        /// SettingsPanel pill and the User-Agent and the UploadClient
        /// gate all agree on what "current" means.
        ///
        /// Normalised to a 3-part Version (Major.Minor.Build) on the
        /// way out. The raw Assembly.GetName().Version is always
        /// 4-part with Revision=0, but TryParseTag emits 3-part
        /// (Revision=-1, the "unset" sentinel) from "v0.1.10"-style
        /// tags. Version.Equals compares all four components, so
        /// without normalisation the staleness IndexOf check inside
        /// Compute would never match a 4-part current version
        /// against a 3-part released-version list — and a user one
        /// release behind would see TooOld instead of SlightlyStale.
        /// </summary>
        public static Version GetCurrentAssemblyVersion()
        {
            var asm = typeof(UpdateChecker).Assembly;
            var raw = asm.GetName().Version ?? new Version(0, 0, 0);
            return NormalizeToThreePart(raw);
        }

        /// <summary>
        /// Strip the Revision component so a Version compares cleanly
        /// against tag-parsed Versions. <c>Version.Build</c> can be -1
        /// (unset) on a 2-part input; we clamp to 0.
        /// </summary>
        internal static Version NormalizeToThreePart(Version v)
        {
            if (v == null) return new Version(0, 0, 0);
            return new Version(
                v.Major < 0 ? 0 : v.Major,
                v.Minor < 0 ? 0 : v.Minor,
                v.Build < 0 ? 0 : v.Build);
        }
    }
}
