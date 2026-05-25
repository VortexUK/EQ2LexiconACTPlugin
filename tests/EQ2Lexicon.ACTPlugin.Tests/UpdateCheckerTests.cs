using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace EQ2Lexicon.ACTPlugin.Tests
{
    /// <summary>
    /// Tests for UpdateChecker — the version-comparison logic that drives
    /// the badge in the settings panel and gates uploads when an install
    /// gets too far behind. Hand-rolled JSON parsing has been bitten by
    /// schema drift before, so the parser branch is exercised against
    /// real-shape GitHub API responses (trimmed for brevity).
    /// </summary>
    public class UpdateCheckerTests
    {
        // ── TryParseTag ────────────────────────────────────────────────────

        [Theory]
        [InlineData("v0.1.7", 0, 1, 7)]
        [InlineData("0.1.7", 0, 1, 7)]
        [InlineData("V1.2.3", 1, 2, 3)]
        [InlineData("v0.1.7-rc1", 0, 1, 7)]    // pre-release suffix stripped
        [InlineData("v0.1.7+build42", 0, 1, 7)] // build metadata stripped
        public void TryParseTag_ParsesCommonShapes(string tag, int major, int minor, int build)
        {
            var v = UpdateChecker.TryParseTag(tag);
            Assert.NotNull(v);
            Assert.Equal(major, v!.Major);
            Assert.Equal(minor, v.Minor);
            Assert.Equal(build, v.Build);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not-a-version")]
        [InlineData("v")]
        [InlineData("vX.Y.Z")]
        public void TryParseTag_ReturnsNullForGarbage(string tag)
        {
            Assert.Null(UpdateChecker.TryParseTag(tag));
        }

        // ── Compute (the core decision matrix) ─────────────────────────────
        //
        // Threshold = 2: latest + 2 prior = "current or slightly stale";
        // older than that = TooOld.

        [Fact]
        public void Compute_Current_WhenEqualToLatest()
        {
            var result = UpdateChecker.Compute(
                new Version(0, 1, 8),
                new[] { new Version(0, 1, 8), new Version(0, 1, 7), new Version(0, 1, 6) });
            Assert.Equal(UpdateStatus.Current, result.Status);
            Assert.Equal("0.1.8", result.CurrentVersion);
            Assert.Equal("0.1.8", result.LatestVersion);
            Assert.Equal(
                "https://github.com/VortexUK/EQ2LexiconACTPlugin/releases/tag/v0.1.8",
                result.LatestReleaseUrl);
            Assert.False(result.UploadBlocked);
        }

        [Fact]
        public void Compute_SlightlyStale_WhenOneVersionBehind()
        {
            var result = UpdateChecker.Compute(
                new Version(0, 1, 7),
                new[] { new Version(0, 1, 8), new Version(0, 1, 7), new Version(0, 1, 6) });
            Assert.Equal(UpdateStatus.SlightlyStale, result.Status);
            Assert.False(result.UploadBlocked);
        }

        [Fact]
        public void Compute_SlightlyStale_WhenTwoVersionsBehind()
        {
            // Exactly at the boundary — must still be allowed.
            var result = UpdateChecker.Compute(
                new Version(0, 1, 6),
                new[] { new Version(0, 1, 8), new Version(0, 1, 7), new Version(0, 1, 6) });
            Assert.Equal(UpdateStatus.SlightlyStale, result.Status);
            Assert.False(result.UploadBlocked);
        }

        [Fact]
        public void Compute_TooOld_WhenThreeVersionsBehind()
        {
            // Just past the boundary — must block.
            var result = UpdateChecker.Compute(
                new Version(0, 1, 5),
                new[]
                {
                    new Version(0, 1, 8), new Version(0, 1, 7),
                    new Version(0, 1, 6), new Version(0, 1, 5),
                });
            Assert.Equal(UpdateStatus.TooOld, result.Status);
            Assert.True(result.UploadBlocked);
        }

        [Fact]
        public void Compute_TooOld_WhenMissingFromRecentList()
        {
            // Current version isn't in the recent list at all — way behind.
            var result = UpdateChecker.Compute(
                new Version(0, 0, 5),
                new[] { new Version(0, 1, 8), new Version(0, 1, 7), new Version(0, 1, 6) });
            Assert.Equal(UpdateStatus.TooOld, result.Status);
        }

        [Fact]
        public void Compute_DevBuild_WhenLocalNewerThanLatest()
        {
            // Happens between bumping <Version> and tagging the release.
            // Local 0.1.9 dev build, latest published is 0.1.8 — don't
            // pester the maintainer with their own version.
            var result = UpdateChecker.Compute(
                new Version(0, 1, 9),
                new[] { new Version(0, 1, 8), new Version(0, 1, 7) });
            Assert.Equal(UpdateStatus.DevBuild, result.Status);
            Assert.False(result.UploadBlocked);
        }

        [Fact]
        public void Compute_Unknown_WhenFeedIsEmpty()
        {
            // GitHub fetch failed / rate-limited / brand-new repo.
            // Caller should NOT block uploads on this.
            var result = UpdateChecker.Compute(new Version(0, 1, 8), new Version[0]);
            Assert.Equal(UpdateStatus.Unknown, result.Status);
            Assert.False(result.UploadBlocked);
        }

        [Fact]
        public void Compute_FourPartCurrentVersionStillMatchesThreePartReleased()
        {
            // Regression test for the v0.1.10-shows-as-TooOld bug.
            //
            // Real-world setup: Assembly.GetName().Version is always
            // 4-part with Revision=0 (e.g. Version(0,1,10,0)).
            // TryParseTag("v0.1.10") emits 3-part with Revision=-1.
            // Version.Equals compares ALL FOUR components, so the
            // IndexOf inside Compute would never match → fell
            // through to TooOld even though the user was one release
            // behind. Bug was real, screenshot at the commit message.
            var result = UpdateChecker.Compute(
                new Version(0, 1, 10, 0),  // 4-part — exactly what Assembly.GetName().Version returns
                new[]
                {
                    new Version(0, 1, 11),  // 3-part — exactly what TryParseTag returns
                    new Version(0, 1, 10),
                    new Version(0, 1, 9),
                });
            Assert.Equal(UpdateStatus.SlightlyStale, result.Status);
            Assert.False(result.UploadBlocked);
        }

        [Fact]
        public void Compute_FourPartCurrentEqualsThreePartLatest_IsCurrentNotDevBuild()
        {
            // Same shape bug from a different angle: when current is
            // 4-part Version(0,1,11,0) and latest is 3-part Version(0,1,11),
            // CompareTo says 0.1.11.0 > 0.1.11 (revision 0 > revision -1)
            // which would (without normalisation) misclassify a fully
            // up-to-date install as DevBuild.
            var result = UpdateChecker.Compute(
                new Version(0, 1, 11, 0),
                new[] { new Version(0, 1, 11), new Version(0, 1, 10) });
            Assert.Equal(UpdateStatus.Current, result.Status);
        }

        [Fact]
        public void NormalizeToThreePart_StripsRevisionAndClampsBuild()
        {
            Assert.Equal(new Version(1, 2, 3), UpdateChecker.NormalizeToThreePart(new Version(1, 2, 3, 0)));
            Assert.Equal(new Version(1, 2, 3), UpdateChecker.NormalizeToThreePart(new Version(1, 2, 3, 999)));
            // 2-part input → Build is -1, must clamp to 0.
            Assert.Equal(new Version(1, 2, 0), UpdateChecker.NormalizeToThreePart(new Version(1, 2)));
            // Null defensiveness.
            Assert.Equal(new Version(0, 0, 0), UpdateChecker.NormalizeToThreePart(null!));
        }

        [Fact]
        public void Compute_HandlesUnsortedInput()
        {
            // Defensive — don't trust the caller to have pre-sorted.
            var result = UpdateChecker.Compute(
                new Version(0, 1, 6),
                new[] { new Version(0, 1, 6), new Version(0, 1, 8), new Version(0, 1, 7) });
            Assert.Equal(UpdateStatus.SlightlyStale, result.Status);
            Assert.Equal("0.1.8", result.LatestVersion);
        }

        // ── ParseReleaseVersions ───────────────────────────────────────────

        [Fact]
        public void ParseReleaseVersions_RealishGitHubShape()
        {
            // Trimmed but real-shape — only the fields we read are guaranteed
            // to exist; the parser must tolerate the rest of the schema.
            var json = @"[
                {
                    ""url"": ""..."",
                    ""tag_name"": ""v0.1.8"",
                    ""name"": ""v0.1.8"",
                    ""draft"": false,
                    ""prerelease"": false
                },
                {
                    ""tag_name"": ""v0.1.7"",
                    ""draft"": false
                },
                {
                    ""tag_name"": ""v0.1.6"",
                    ""draft"": false
                }
            ]";
            var versions = UpdateChecker.ParseReleaseVersions(json);
            Assert.Equal(3, versions.Count);
            Assert.Equal(new Version(0, 1, 8), versions[0]);
            Assert.Equal(new Version(0, 1, 7), versions[1]);
            Assert.Equal(new Version(0, 1, 6), versions[2]);
        }

        [Fact]
        public void ParseReleaseVersions_SkipsDrafts()
        {
            // The release workflow creates DRAFT releases — they shouldn't
            // be considered "the latest" until a human publishes them.
            var json = @"[
                {""tag_name"": ""v0.1.9"", ""draft"": true},
                {""tag_name"": ""v0.1.8"", ""draft"": false}
            ]";
            var versions = UpdateChecker.ParseReleaseVersions(json);
            Assert.Single(versions);
            Assert.Equal(new Version(0, 1, 8), versions[0]);
        }

        [Fact]
        public void ParseReleaseVersions_SkipsNonNumericTags()
        {
            // Whatever shape someone tags in the future ("nightly", "preview"),
            // it shouldn't crash the parse — just be skipped.
            var json = @"[
                {""tag_name"": ""nightly"", ""draft"": false},
                {""tag_name"": ""v0.1.8"", ""draft"": false}
            ]";
            var versions = UpdateChecker.ParseReleaseVersions(json);
            Assert.Single(versions);
        }

        [Theory]
        [InlineData("")]
        [InlineData("[]")]
        [InlineData("not json at all")]
        [InlineData("{\"message\": \"API rate limit exceeded\"}")]
        public void ParseReleaseVersions_ReturnsEmptyForUnhelpfulInputs(string json)
        {
            // Any of these should yield [] not throw — the caller will fall
            // through to UpdateStatus.Unknown and uploads stay allowed.
            var versions = UpdateChecker.ParseReleaseVersions(json);
            Assert.Empty(versions);
        }

        // ── ParseLatestDllAsset (self-update flow) ────────────────────────

        [Fact]
        public void ParseLatestDllAsset_PullsUrlAndDigestFromFirstNonDraft()
        {
            // Real GH release-API shape (trimmed). Two releases — second
            // is a draft and should be skipped. We expect the v0.1.11
            // asset URL + the sha256 hex without the algo prefix.
            var json = @"[
                {
                    ""tag_name"": ""v0.1.12"",
                    ""draft"": false,
                    ""assets"": [
                        {
                            ""name"": ""EQ2Lexicon.ACTPlugin.dll"",
                            ""browser_download_url"": ""https://github.com/VortexUK/EQ2LexiconACTPlugin/releases/download/v0.1.12/EQ2Lexicon.ACTPlugin.dll"",
                            ""digest"": ""sha256:f44ef3243eb0b6392132894d3b535da6aa0a17a933a60ba72c9bbcb925cdb270""
                        }
                    ]
                },
                {
                    ""tag_name"": ""v0.1.13"",
                    ""draft"": true,
                    ""assets"": [
                        {""name"": ""EQ2Lexicon.ACTPlugin.dll""}
                    ]
                }
            ]";
            var (url, sha) = UpdateChecker.ParseLatestDllAsset(json);
            Assert.Equal(
                "https://github.com/VortexUK/EQ2LexiconACTPlugin/releases/download/v0.1.12/EQ2Lexicon.ACTPlugin.dll",
                url);
            Assert.Equal("f44ef3243eb0b6392132894d3b535da6aa0a17a933a60ba72c9bbcb925cdb270", sha);
        }

        [Fact]
        public void ParseLatestDllAsset_ReturnsEmptyWhenDigestMissing()
        {
            // Sometimes the digest field is absent (pre-2024 releases,
            // very fresh releases before GH backfills). Installer must
            // refuse to auto-stage in that case — having no digest
            // string is the signal.
            var json = @"[{
                ""tag_name"": ""v0.1.12"",
                ""draft"": false,
                ""assets"": [
                    {
                        ""name"": ""EQ2Lexicon.ACTPlugin.dll"",
                        ""browser_download_url"": ""https://example/EQ2Lexicon.ACTPlugin.dll""
                    }
                ]
            }]";
            var (url, sha) = UpdateChecker.ParseLatestDllAsset(json);
            Assert.Equal("https://example/EQ2Lexicon.ACTPlugin.dll", url);
            Assert.Equal("", sha);
        }

        [Fact]
        public void ParseLatestDllAsset_SkipsNonDllAssets()
        {
            // Hypothetical future: a release attaches additional assets
            // (signature file, source zip). Pick the .dll one specifically.
            var json = @"[{
                ""tag_name"": ""v0.1.12"",
                ""draft"": false,
                ""assets"": [
                    {""name"": ""checksums.txt"", ""browser_download_url"": ""https://example/checksums.txt"", ""digest"": ""sha256:aaa""},
                    {""name"": ""EQ2Lexicon.ACTPlugin.dll"", ""browser_download_url"": ""https://example/plugin.dll"", ""digest"": ""sha256:bbb""}
                ]
            }]";
            var (url, sha) = UpdateChecker.ParseLatestDllAsset(json);
            Assert.Equal("https://example/plugin.dll", url);
            Assert.Equal("bbb", sha);
        }

        [Fact]
        public void ParseLatestDllAsset_ReturnsEmptyForEmptyFeed()
        {
            Assert.Equal(("", ""), UpdateChecker.ParseLatestDllAsset(""));
            Assert.Equal(("", ""), UpdateChecker.ParseLatestDllAsset("[]"));
            Assert.Equal(("", ""), UpdateChecker.ParseLatestDllAsset("not json"));
        }

        [Fact]
        public void ParseLatestDllAsset_IgnoresDllNamedAuthorOrUploader()
        {
            // Audit M1: pre-v0.1.13, FindDllAsset walked every "name"
            // field in the release block. A maintainer with a display
            // name ending in ".dll" (GitHub usernames allow dots) would
            // be picked up as a fake asset → ParseLatestDllAsset would
            // return ("", "") because no browser_download_url adjacent
            // to the author's name field. Now scoped to "assets":[...].
            var json = @"[{
                ""tag_name"": ""v0.1.12"",
                ""draft"": false,
                ""author"": {""name"": ""malicious-actor.dll"", ""url"": ""https://evil/""},
                ""uploader"": {""name"": ""another.dll""},
                ""assets"": [
                    {
                        ""name"": ""EQ2Lexicon.ACTPlugin.dll"",
                        ""browser_download_url"": ""https://example/legit.dll"",
                        ""digest"": ""sha256:legit""
                    }
                ]
            }]";
            var (url, sha) = UpdateChecker.ParseLatestDllAsset(json);
            Assert.Equal("https://example/legit.dll", url);
            Assert.Equal("legit", sha);
        }

        [Fact]
        public void ParseLatestDllAsset_HandlesReleaseBodyContainingTagNameLiteral()
        {
            // Audit M1: pre-v0.1.13, the block-end was approximated as
            // "the next '\"tag_name\"' substring". A release whose body
            // text contained that literal (e.g. a copy-pasted JSON
            // snippet in the release notes) would split the block early
            // and miss the assets array. Now we scope by finding the
            // "assets":[ ... ] array via bracket-counting.
            var json = @"[{
                ""tag_name"": ""v0.1.12"",
                ""body"": ""Some notes mentioning \""tag_name\"": \""v9.9.9\"" as an example."",
                ""draft"": false,
                ""assets"": [
                    {
                        ""name"": ""EQ2Lexicon.ACTPlugin.dll"",
                        ""browser_download_url"": ""https://example/x.dll"",
                        ""digest"": ""sha256:abc""
                    }
                ]
            }]";
            var (url, sha) = UpdateChecker.ParseLatestDllAsset(json);
            Assert.Equal("https://example/x.dll", url);
            Assert.Equal("abc", sha);
        }

        [Fact]
        public void ParseLatestDllAsset_DigestPrefixCaseInsensitive()
        {
            // GH consistently uses lowercase "sha256:" but pin the
            // prefix match as case-insensitive so a future variation
            // (SHA256:) doesn't silently empty the digest.
            var json = @"[{
                ""tag_name"": ""v0.1.12"",
                ""draft"": false,
                ""assets"": [{
                    ""name"": ""x.dll"",
                    ""browser_download_url"": ""https://e/x.dll"",
                    ""digest"": ""SHA256:DEADBEEF""
                }]
            }]";
            var (_, sha) = UpdateChecker.ParseLatestDllAsset(json);
            Assert.Equal("deadbeef", sha);
        }

        // ── CheckAsync (end-to-end orchestration) ─────────────────────────

        private class StubFetcher : IReleaseFeedFetcher
        {
            public string Response { get; set; } = "";
            public Exception? Throw { get; set; }
            public Task<string> FetchAsync()
            {
                if (Throw != null) throw Throw;
                return Task.FromResult(Response);
            }
        }

        [Fact]
        public async Task CheckAsync_ReturnsUnknown_OnFetcherException()
        {
            // A 500 from GitHub / connection reset / DNS failure must NOT
            // surface as a crash — the gate has to fail open or we'd
            // accidentally brick everyone on a GitHub incident.
            var fetcher = new StubFetcher { Throw = new InvalidOperationException("boom") };
            var result = await UpdateChecker.CheckAsync(new Version(0, 1, 8), fetcher);
            Assert.Equal(UpdateStatus.Unknown, result.Status);
            Assert.False(result.UploadBlocked);
        }

        [Fact]
        public async Task CheckAsync_ReturnsUnknown_OnEmptyResponse()
        {
            // Production fetcher returns "" on non-200 (e.g. 403 rate
            // limit). Must not be mistaken for "no releases exist".
            var fetcher = new StubFetcher { Response = "" };
            var result = await UpdateChecker.CheckAsync(new Version(0, 1, 8), fetcher);
            Assert.Equal(UpdateStatus.Unknown, result.Status);
        }

        [Fact]
        public async Task CheckAsync_HappyPath_ReturnsCurrent()
        {
            var fetcher = new StubFetcher
            {
                Response = @"[
                    {""tag_name"": ""v0.1.8"", ""draft"": false},
                    {""tag_name"": ""v0.1.7"", ""draft"": false}
                ]",
            };
            var result = await UpdateChecker.CheckAsync(new Version(0, 1, 8), fetcher);
            Assert.Equal(UpdateStatus.Current, result.Status);
            Assert.Equal("0.1.8", result.LatestVersion);
        }
    }
}
