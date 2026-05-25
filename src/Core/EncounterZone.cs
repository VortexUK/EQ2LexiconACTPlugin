using System;

namespace EQ2Lexicon.ACTPlugin
{
    /// <summary>
    /// Classifies ACT zone names. Pure (no ACT references) so the test
    /// project can exercise the predicate set without ACT installed.
    /// </summary>
    public static class EncounterZone
    {
        /// <summary>
        /// True when the zone is ACT's synthetic "Import/Merge" bucket —
        /// where imported logs and merged/edited encounters end up. The
        /// plugin must never upload from this zone because:
        ///
        ///   * Merged encounters are USER-CUSTOMISED — combining two real
        ///     fights produces an aggregate that doesn't correspond to
        ///     any single in-game event. Uploading it would pollute the
        ///     leaderboard with parses that didn't actually happen as
        ///     shown.
        ///   * Imported parses are old log replays — the user already
        ///     had the chance to upload them when they happened. Doing
        ///     it on import would back-date arbitrary historical fights.
        ///
        /// Applied in both directions:
        ///   * EncounterCapture.Poll skips with a visible reason.
        ///   * ActMenuExtension.OnMenuOpening greys out the right-click
        ///     menu item.
        ///   * Plugin.OnManualUploadRequested re-checks defensively (in
        ///     case a fast click races the menu's greyed-out state).
        /// </summary>
        public static bool IsImportOrMerge(string? zoneName)
        {
            if (string.IsNullOrWhiteSpace(zoneName)) return false;
            return string.Equals(zoneName.Trim(), "Import/Merge", StringComparison.OrdinalIgnoreCase);
        }
    }
}
