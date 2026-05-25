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
    }
}
