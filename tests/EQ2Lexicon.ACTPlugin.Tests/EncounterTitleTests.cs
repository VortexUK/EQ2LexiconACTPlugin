using Xunit;

namespace EQ2Lexicon.ACTPlugin.Tests
{
    /// <summary>
    /// Pins the placeholder-title set. The polling path defers
    /// placeholders for up to a minute; the manual right-click path
    /// rejects them outright. Both call this same predicate, so a
    /// silent change to the list affects user-visible behaviour in
    /// both flows.
    ///
    /// If you add a new placeholder (e.g. a future ACT version emits
    /// "Pending" for evac-cut fights), add it to BOTH the predicate
    /// and one of the Theory rows below.
    /// </summary>
    public class EncounterTitleTests
    {
        [Theory]
        [InlineData("Encounter")]        // the observed placeholder on evac
        [InlineData("encounter")]        // case-insensitive
        [InlineData("ENCOUNTER")]
        [InlineData("  Encounter  ")]    // whitespace tolerant
        [InlineData("Unknown")]          // defensive — historical ACT output
        [InlineData("unknown")]
        [InlineData("")]                 // empty = "ACT hasn't named it"
        [InlineData("   ")]              // whitespace-only = empty
        [InlineData(null)]               // null safe
        public void IsPlaceholder_TrueForKnownPlaceholders(string? title)
        {
            Assert.True(EncounterTitle.IsPlaceholder(title));
        }

        [Theory]
        [InlineData("a krait patriarch")]
        [InlineData("Sullon Zek")]
        [InlineData("Trash Pull")]
        [InlineData("Encounter with a krait")]  // not a bare placeholder
        [InlineData("Unknown Caller")]          // substring of placeholder is fine
        [InlineData("E")]                       // single-char real title
        public void IsPlaceholder_FalseForRealMobNames(string title)
        {
            Assert.False(EncounterTitle.IsPlaceholder(title));
        }

        // ── MatchesAnEnemy ─────────────────────────────────────────────────
        //
        // Heuristic detector for "user renamed this encounter in ACT".
        // ACT's right-click → Rename Encounter literally just assigns to
        // EncounterData.Title and leaves no audit trail (confirmed
        // empirically via reflection — every title-bearing property
        // mutates together). The only signal we have is that an
        // auto-derived title always matches an actual enemy combatant
        // in the fight; a rename usually doesn't.

        [Theory]
        [InlineData("Pawbuster", "Pawbuster")]                         // exact match
        [InlineData("pawbuster", "Pawbuster")]                         // case-insensitive
        [InlineData("  Pawbuster  ", "Pawbuster")]                     // trim both sides
        [InlineData("Pawbuster", "Pawbuster the Crusher")]             // title is substring of enemy (EQ2 epithet)
        [InlineData("Pawbuster the Crusher", "Pawbuster")]             // enemy is substring of title (ACT short-name)
        [InlineData("a krait patriarch", "a krait patriarch")]         // EQ2 lowercase article-prefixed name
        [InlineData("Pawbuster (Wipe 1)", "Pawbuster")]                // user-appended note still passes
        public void MatchesAnEnemy_TrueForRealisticAutoTitles(string title, string enemyName)
        {
            Assert.True(EncounterTitle.MatchesAnEnemy(title, new[] { enemyName }));
        }

        [Fact]
        public void MatchesAnEnemy_TrueWhenAnyEnemyInListMatches()
        {
            // Multi-enemy fight — title only needs to match ONE of them.
            // Mirrors a boss + adds encounter where the title is the boss
            // and the adds are also in the combatant list.
            var enemies = new[] { "an adder", "a wisp", "Pawbuster the Crusher" };
            Assert.True(EncounterTitle.MatchesAnEnemy("Pawbuster", enemies));
        }

        [Theory]
        [InlineData("TESTING", "a krait patriarch")]       // user renamed to "TESTING" — the diagnostic-session smoking gun
        [InlineData("Pawbuster", "a brain magnate")]       // title is a real boss but the fight was something else
        [InlineData("dps test", "training dummy")]         // user labelling a parse run
        public void MatchesAnEnemy_FalseWhenTitleIsClearlyRenamed(string title, string enemyName)
        {
            Assert.False(EncounterTitle.MatchesAnEnemy(title, new[] { enemyName }));
        }

        [Fact]
        public void MatchesAnEnemy_FalseForEmptyTitle()
        {
            // Empty / whitespace title shouldn't be considered a match
            // even if there's an empty-string enemy in the list. Belt-
            // and-brace — Plugin already filters placeholder titles via
            // IsPlaceholder before this check ever runs, but a future
            // call site might not.
            Assert.False(EncounterTitle.MatchesAnEnemy("", new[] { "Pawbuster" }));
            Assert.False(EncounterTitle.MatchesAnEnemy("   ", new[] { "Pawbuster" }));
            Assert.False(EncounterTitle.MatchesAnEnemy(null, new[] { "Pawbuster" }));
        }

        [Fact]
        public void MatchesAnEnemy_FalseWhenNoEnemiesProvided()
        {
            // Caller must pass at least one enemy name for the heuristic
            // to mean anything. No enemies = nothing to compare against —
            // treat as "doesn't match" so the upload is gated.
            Assert.False(EncounterTitle.MatchesAnEnemy("Pawbuster", System.Array.Empty<string>()));
            Assert.False(EncounterTitle.MatchesAnEnemy("Pawbuster", new string?[] { null, "" }));
            Assert.False(EncounterTitle.MatchesAnEnemy("Pawbuster", null));
        }

        [Fact]
        public void MatchesAnEnemy_SkipsNullAndEmptyEnemyEntries()
        {
            // A mix of legit + junk entries — the real ones still drive
            // the answer; null / whitespace shouldn't false-positive.
            var enemies = new string?[] { null, "  ", "", "Pawbuster the Crusher" };
            Assert.True(EncounterTitle.MatchesAnEnemy("Pawbuster", enemies));
        }
    }
}
