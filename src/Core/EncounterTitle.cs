using System;
using System.Collections.Generic;

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

        /// <summary>
        /// True when <paramref name="title"/> appears as (or within) one
        /// of the supplied enemy combatant names. The expected guarantee
        /// is that ACT's auto-derived title for an encounter is always
        /// the name of an enemy that was actually fought — so a title
        /// that matches NO enemy is a strong signal the user manually
        /// renamed the encounter via right-click → Rename Encounter in
        /// ACT.
        ///
        /// Confirmed empirically (the diagnostic-dump session that led
        /// to this code) that ACT preserves no original-title shadow,
        /// no `Tags` marker, and no `HistoryRecord` audit field — every
        /// title-bearing property mutates with the rename. The
        /// combatant-list cross-check is the only signal we have.
        ///
        /// Matching is permissive in both directions to handle EQ2's
        /// "Boss the Epithet" mob naming style:
        ///   * exact (case-insensitive): "Pawbuster" == "Pawbuster"
        ///   * title-is-substring-of-enemy: title "Pawbuster" matches
        ///     enemy "Pawbuster the Crusher"
        ///   * enemy-is-substring-of-title: title "Pawbuster (Wipe 1)"
        ///     matches enemy "Pawbuster"
        ///
        /// Both directions are needed because:
        ///   * ACT sometimes uses the short form ("Pawbuster") even
        ///     when the EQ2 log gives the full epithet,
        ///   * and a legitimate user note appended to the title
        ///     ("Pawbuster - take 2") shouldn't trip the filter.
        ///
        /// Returns false when the title is null/whitespace, or when
        /// <paramref name="enemyNames"/> is empty (no enemies = nothing
        /// to compare against, treat as suspect).
        ///
        /// Whitespace at the edges is trimmed on both sides before
        /// comparing — copy-pasted-with-trailing-space titles shouldn't
        /// false-positive.
        /// </summary>
        public static bool MatchesAnEnemy(string? title, IEnumerable<string?>? enemyNames)
        {
            if (string.IsNullOrWhiteSpace(title)) return false;
            if (enemyNames == null) return false;
            var t = title.Trim();
            foreach (var name in enemyNames)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                var n = name.Trim();
                if (n.Equals(t, StringComparison.OrdinalIgnoreCase)) return true;
                if (n.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (t.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }
    }
}
