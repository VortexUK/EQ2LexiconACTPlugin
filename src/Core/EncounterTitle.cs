using System;

namespace EQ2Lexicon.ACTPlugin
{
    /// <summary>
    /// Classifies ACT encounter titles. Lives in Core (not the
    /// UI-coupled EncounterCapture) so tests can pin the placeholder
    /// list without needing ACT installed — the exact set quietly
    /// matters: a new placeholder ACT emits that we don't recognise
    /// would result in a real fight being uploaded under a useless name.
    /// </summary>
    public static class EncounterTitle
    {
        /// <summary>
        /// True for titles that mean "ACT hasn't filled this in yet".
        /// The auto-upload path defers placeholders for up to
        /// MaxPlaceholderWaitSeconds; the manual right-click path
        /// rejects them outright with a "rename it in ACT first" error.
        ///
        /// Observed placeholders to date:
        ///   * "Encounter"  — fights cut short by /evac before ACT's
        ///                    EQ2 log scanner names the mob
        ///   * "Unknown"    — defensive; ACT has emitted this in old
        ///                    forum threads though we haven't seen it
        ///                    on the current EQ2 parser
        ///   * null / empty / whitespace — equivalent to a placeholder
        ///
        /// Case-insensitive; tolerant of surrounding whitespace.
        /// </summary>
        public static bool IsPlaceholder(string? title)
        {
            if (string.IsNullOrWhiteSpace(title)) return true;
            var t = title.Trim();
            return string.Equals(t, "Encounter", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t, "Unknown", StringComparison.OrdinalIgnoreCase);
        }
    }
}
