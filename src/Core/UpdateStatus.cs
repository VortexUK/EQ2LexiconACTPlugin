namespace EQ2Lexicon.ACTPlugin
{
    /// <summary>
    /// Where this plugin's installed version sits relative to the most
    /// recent published GitHub release.
    ///
    /// Used to drive the version pill in the settings panel and to gate
    /// uploads when the local install is too far behind to be trusted to
    /// produce well-formed payloads.
    /// </summary>
    public enum UpdateStatus
    {
        /// <summary>
        /// We haven't successfully checked yet (first run, GitHub fetch
        /// failed, no network, rate-limited, etc.). UI treats this as
        /// "neutral / no banner"; uploads are allowed — we don't want a
        /// network outage to silently block parses.
        /// </summary>
        Unknown,

        /// <summary>Running the latest published release.</summary>
        Current,

        /// <summary>
        /// 1-2 versions behind the latest release. Uploads still work; UI
        /// shows a yellow nudge to update.
        /// </summary>
        SlightlyStale,

        /// <summary>
        /// More than 2 versions behind. Uploads are blocked and the UI
        /// shows a red banner with a download link. The threshold is
        /// <see cref="UpdateChecker.StaleThresholdVersions"/>.
        /// </summary>
        TooOld,

        /// <summary>
        /// Local version is newer than anything published — i.e. a dev
        /// build. Treat as Current for gating purposes; UI shows a muted
        /// "dev" label so we don't pester maintainers with self-prompts.
        /// </summary>
        DevBuild,
    }

    /// <summary>
    /// Outcome of an <see cref="UpdateChecker"/> run. Plain data so it
    /// crosses the UI boundary cleanly.
    /// </summary>
    public class UpdateCheckResult
    {
        public UpdateStatus Status { get; set; } = UpdateStatus.Unknown;

        /// <summary>Version this plugin DLL identifies as (assembly version).</summary>
        public string CurrentVersion { get; set; } = "";

        /// <summary>
        /// Latest version known on GitHub. Empty when <see cref="Status"/>
        /// is <see cref="UpdateStatus.Unknown"/> (we couldn't fetch).
        /// </summary>
        public string LatestVersion { get; set; } = "";

        /// <summary>
        /// Direct URL to the latest release page on GitHub, suitable for
        /// the "Download" button in the settings panel. Empty when no
        /// latest version was resolved.
        /// </summary>
        public string LatestReleaseUrl { get; set; } = "";

        /// <summary>
        /// Direct browser_download_url for the EQ2Lexicon.ACTPlugin.dll
        /// asset of the latest release. Populated when we successfully
        /// parsed an asset matching the DLL filename from the GitHub
        /// release feed. Empty when no asset was found — the in-place
        /// "Install update" button stays disabled and the user falls
        /// back to the browser download.
        /// </summary>
        public string LatestDllUrl { get; set; } = "";

        /// <summary>
        /// Lowercase-hex SHA-256 digest of the latest DLL asset,
        /// pulled from the release feed's `digest` field. Used by
        /// PluginUpdater to verify the bytes match before staging.
        /// Empty when GitHub hasn't computed one yet (very recent
        /// releases) or the feed shape changes — in that case the
        /// installer must refuse to auto-stage rather than ship an
        /// unverified binary.
        /// </summary>
        public string LatestDllSha256 { get; set; } = "";

        /// <summary>
        /// True when <see cref="Status"/> is <see cref="UpdateStatus.TooOld"/>.
        /// Centralised so UploadClient and SettingsPanel agree on what
        /// "blocked" means without duplicating the predicate.
        /// </summary>
        public bool UploadBlocked => Status == UpdateStatus.TooOld;
    }
}
