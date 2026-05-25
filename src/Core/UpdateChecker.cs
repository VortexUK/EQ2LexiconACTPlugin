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
            var result = new UpdateCheckResult
            {
                CurrentVersion = currentVersion?.ToString(3) ?? "",
            };

            if (releasedVersions == null || releasedVersions.Count == 0)
            {
                result.Status = UpdateStatus.Unknown;
                return result;
            }

            // Sort descending so [0] is the newest, regardless of what
            // the caller passed in. GitHub already returns newest-first
            // but we don't rely on that here.
            var sorted = releasedVersions
                .Where(v => v != null)
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

            int cmp = currentVersion.CompareTo(latest);
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
            int allowedRecent = StaleThresholdVersions + 1;
            int idxInRecent = sorted.Take(allowedRecent).ToList().IndexOf(currentVersion);
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
                return Compute(currentVersion ?? new Version(0, 0, 0), versions);
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
        /// </summary>
        public static Version GetCurrentAssemblyVersion()
        {
            var asm = typeof(UpdateChecker).Assembly;
            return asm.GetName().Version ?? new Version(0, 0, 0);
        }
    }
}
